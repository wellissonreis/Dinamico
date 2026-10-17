using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Transport;

namespace Dinamico.LoadEngine.Metrics;

/// <summary>Serializes event registration and rotation. Use the same monotonic clock as transport.</summary>
public sealed class MetricWindowCollector
{
    private readonly object gate = new();
    private readonly IMonotonicClock clock;
    private readonly long t0;
    private readonly MetricWindowSink sink;
    private ulong index, inFlight, registrations, delaySum, delayMax;
    private long lastTicks;
    private ulong[] counters = new ulong[MetricWindow.CounterCount];
    private ulong[] statuses = new ulong[MetricWindow.StatusCount];
    private ulong[] retryKinds = new ulong[MetricWindow.RetryKindCount];
    private ulong clamped, uncertain;
    private ulong? minRetry, maxRetry;
    private LogHistogram terminal = new(), completed = new();
    private MeasurementQuality quality;
    private bool faulted, closed;
    private MetricWindowManifest? manifest;

    public MetricWindowCollector(IMonotonicClock clock, long startingTimestamp, MetricWindowSink sink)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        t0 = startingTimestamp;
    }

    public bool IsFaulted { get { lock (gate) return faulted; } }
    public MeasurementQuality Quality { get { lock (gate) return quality; } }
    public MetricWindowManifest? Manifest { get { lock (gate) return manifest; } }

    // Due is partitioned atomically into started or one exclusive drop reason.
    public void RecordStarted(long observedTimestamp) => Register(observedTimestamp, () =>
    {
        ulong due = checked(counters[0] + 1), started = checked(counters[1] + 1), next = checked(inFlight + 1);
        counters[0] = due;
        counters[1] = started;
        inFlight = next;
    });

    public void RecordNotStarted(NotStartedReason reason, ulong count, long observedTimestamp)
    {
        int counter = reason switch
        {
            NotStartedReason.SchedulerLate => 2,
            NotStartedReason.ConcurrencyLimit => 3,
            NotStartedReason.Policy => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        if (count == 0) return;
        Register(observedTimestamp, () =>
        {
            ulong due = checked(counters[0] + count), drops = checked(counters[counter] + count);
            counters[0] = due;
            counters[counter] = drops;
            quality |= MeasurementQuality.GeneratorLimited;
        });
    }

    public void RecordHeaders(ResponseHeadersObservation observation)
    {
        if (observation.StatusCode is < 200 or > 599)
            throw new ArgumentException("Invalid headers observation.", nameof(observation));
        // Validate the evidence association, including default/unsupported enum values.
        _ = new ResponseHeadersObservation(observation.ArrivalIndex, observation.StatusCode,
            observation.RetryAfter, observation.ObservedTimestamp);
        int statusIndex = observation.StatusCode - 100, kind = (int)observation.RetryAfter.Kind;
        if (kind is < 0 or >= MetricWindow.RetryKindCount) throw new ArgumentException("Invalid Retry-After kind.");
        Register(observation.ObservedTimestamp, () =>
        {
            if (inFlight == 0) throw new InvalidOperationException("Headers require an admitted operation.");
            ulong nextStatus = checked(statuses[statusIndex] + 1), nextKind = checked(retryKinds[kind] + 1);
            ulong nextClamp = checked(clamped + (observation.RetryAfter.WasClamped ? 1UL : 0));
            ulong nextUncertain = checked(uncertain + (observation.RetryAfter.ClockUncertain ? 1UL : 0));
            statuses[statusIndex] = nextStatus;
            retryKinds[kind] = nextKind;
            clamped = nextClamp;
            uncertain = nextUncertain;
            if (observation.RetryAfter.Delay is { } delay)
            {
                ulong ticks = checked((ulong)delay.Ticks);
                minRetry = minRetry is { } min ? Math.Min(min, ticks) : ticks;
                maxRetry = maxRetry is { } max ? Math.Max(max, ticks) : ticks;
            }
            if (observation.RetryAfter.ClockUncertain) quality |= MeasurementQuality.ClockUncertain;
            if (observation.StatusCode >= 400) quality |= MeasurementQuality.TargetErrors;
        });
    }

    public void RecordTerminal(RequestObservation observation, long observedTimestamp)
    {
        int counter = observation.Outcome switch
        {
            RequestOutcome.HttpCompleted => 5,
            RequestOutcome.TransportFailed => 7,
            RequestOutcome.RequestTimedOut => 8,
            RequestOutcome.CancelledInFlight => 9,
            RequestOutcome.ResponseLimitExceeded => 10,
            RequestOutcome.InternalFailed => 11,
            _ => throw new ArgumentException("Invalid terminal observation.", nameof(observation))
        };
        ulong nanoseconds = checked((ulong)observation.TerminalDuration.Ticks * 100);
        int bucket = LogHistogramSchema.Classify(nanoseconds); // Outside the registration lock.
        Register(observedTimestamp, () =>
        {
            if (inFlight == 0) throw new InvalidOperationException("Terminal requires an admitted operation.");
            ulong next = checked(counters[counter] + 1);
            ulong bytes = checked(counters[12] + (ulong)observation.ResponseBodyBytes);
            ulong success = checked(counters[6] + (observation.IsAccepted(HttpSuccessRule.Completed2xxV1) ? 1UL : 0));
            terminal.ValidateRecord(nanoseconds, bucket);
            if (counter == 5) completed.ValidateRecord(nanoseconds, bucket);
            terminal.RecordClassified(nanoseconds, bucket);
            if (counter == 5) completed.RecordClassified(nanoseconds, bucket);
            counters[counter] = next;
            counters[12] = bytes;
            counters[6] = success;
            inFlight--;
            // Status and Retry-After deliberately are not counted at the terminal event.
        });
    }

    public void Advance()
    {
        lock (gate)
        {
            EnsureOpen();
            try { Rotate(ReadTicks()); }
            catch { Fault(); throw; }
        }
    }

    public MetricWindowManifest Close()
    {
        lock (gate)
        {
            if (closed) return manifest!.Value;
            EnsureOpen();
            try
            {
                long now = ReadTicks();
                // At a boundary the preceding full interval is the final one; no zero-width extra window.
                while (now > EndTicks()) Publish(EndTicks(), false);
                if (index == 0 || now > checked((long)index * MetricWindow.Width.Ticks)) Publish(now, true);
                closed = true;
                manifest = new(index - 1, TimeSpan.FromTicks(now), true, quality);
                return manifest.Value;
            }
            catch
            {
                Fault();
                manifest = new(index == 0 ? 0 : index - 1, TimeSpan.FromTicks(lastTicks), false, quality);
                throw;
            }
        }
    }

    private void Register(long observedTimestamp, Action apply)
    {
        lock (gate)
        {
            EnsureOpen();
            try
            {
                long nowTimestamp = clock.GetTimestamp();
                long now = ReadTicks(nowTimestamp);
                ulong delay = checked((ulong)clock.GetElapsedTime(observedTimestamp, nowTimestamp).Ticks * 100);
                Rotate(now);
                ulong nextRegistrations = checked(registrations + 1), nextDelay = checked(delaySum + delay);
                apply();
                registrations = nextRegistrations;
                delaySum = nextDelay;
                delayMax = Math.Max(delayMax, delay);
            }
            catch { Fault(); throw; }
        }
    }

    private long ReadTicks() => ReadTicks(clock.GetTimestamp());
    private long ReadTicks(long timestamp)
    {
        long ticks = clock.GetElapsedTime(t0, timestamp).Ticks;
        if (ticks < lastTicks || ticks < 0) throw new InvalidOperationException("Monotonic time moved backwards.");
        return lastTicks = ticks;
    }
    private long EndTicks() => checked(((long)index + 1) * MetricWindow.Width.Ticks);
    private void Rotate(long now) { while (now >= EndTicks()) Publish(EndTicks(), false); }
    private void Publish(long end, bool final)
    {
        var window = new MetricWindow(index, checked((long)index * MetricWindow.Width.Ticks), end, final,
            inFlight, quality, counters, statuses, retryKinds, clamped, uncertain, minRetry, maxRetry,
            registrations, delaySum, delayMax, terminal, completed);
        if (!sink.TryWrite(window)) throw new MetricBackpressureException();
        index = checked(index + 1);
        counters = new ulong[MetricWindow.CounterCount];
        statuses = new ulong[MetricWindow.StatusCount];
        retryKinds = new ulong[MetricWindow.RetryKindCount];
        terminal = new();
        completed = new();
        clamped = uncertain = registrations = delaySum = delayMax = 0;
        minRetry = maxRetry = null;
    }
    private void Fault()
    {
        faulted = true;
        quality |= MeasurementQuality.MetricsPartial;
        manifest = new(index == 0 ? 0 : index - 1, TimeSpan.FromTicks(lastTicks), false, quality);
    }
    private void EnsureOpen()
    {
        if (faulted) throw new InvalidOperationException("Metrics are partial; collector cannot accept more events.");
        if (closed) throw new InvalidOperationException("Metric collector is closed.");
    }
}
