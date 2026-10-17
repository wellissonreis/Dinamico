using System.Collections.Immutable;
using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Metrics;

public enum MetricCounter
{
    ScheduledDue, Started, SchedulerLate, ConcurrencyLimit, Policy,
    HttpCompleted, HttpSuccess, TransportFailed, RequestTimedOut, CancelledInFlight,
    ResponseLimitExceeded, InternalFailed, ResponseBodyBytes
}

/// <summary>No mutation API and no mutable histogram reference escapes a closed window.</summary>
public sealed class HistogramSnapshot
{
    private readonly LogHistogram histogram;
    internal HistogramSnapshot(LogHistogram ownedHistogram) => histogram = ownedHistogram;
    public ulong Count => histogram.Count;
    public ulong SumNanoseconds => histogram.SumNanoseconds;
    public ulong? MinimumNanoseconds => histogram.MinimumNanoseconds;
    public ulong? MaximumNanoseconds => histogram.MaximumNanoseconds;
    public ulong OverflowCount => histogram.OverflowCount;
    public ReadOnlySpan<ulong> Counts => histogram.Counts;
    public HistogramQuantile? P50 => histogram.P50;
    public HistogramQuantile? P95 => histogram.P95;
    public HistogramQuantile? P99 => histogram.P99;
    internal LogHistogram Histogram => histogram;
}

public sealed class MetricWindow
{
    public const uint SchemaVersion = 1;
    public const int CounterCount = 13;
    public const int StatusCount = 501;
    public const int RetryKindCount = 6;
    public static readonly TimeSpan Width = TimeSpan.FromSeconds(5);

    internal MetricWindow(ulong index, long startTicks, long endTicks, bool isFinal, ulong inFlight,
        MeasurementQuality quality, ulong[] counters, ulong[] statuses, ulong[] retryKinds,
        ulong retryClamped, ulong retryClockUncertain, ulong? minRetryTicks, ulong? maxRetryTicks,
        ulong registrations, ulong registrationDelaySum, ulong registrationDelayMax,
        LogHistogram terminal, LogHistogram completed)
    {
        WindowIndex = Sequence = index;
        Start = TimeSpan.FromTicks(startTicks);
        End = TimeSpan.FromTicks(endTicks);
        IsFinal = isFinal;
        InFlight = inFlight;
        Quality = quality;
        Counters = ImmutableArray.Create(counters);
        StatusReceived = ImmutableArray.Create(statuses);
        RetryAfterKinds = ImmutableArray.Create(retryKinds);
        RetryAfterClamped = retryClamped;
        RetryAfterClockUncertain = retryClockUncertain;
        MinimumRetryAfter = minRetryTicks is { } min ? TimeSpan.FromTicks(checked((long)min)) : null;
        MaximumRetryAfter = maxRetryTicks is { } max ? TimeSpan.FromTicks(checked((long)max)) : null;
        Registrations = registrations;
        RegistrationDelaySumNanoseconds = registrationDelaySum;
        RegistrationDelayMaxNanoseconds = registrationDelayMax;
        TerminalDuration = new(terminal);
        HttpCompletedDuration = new(completed);
    }

    public ulong Sequence { get; }
    public ulong WindowIndex { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public bool IsFinal { get; }
    public bool IsPartialInterval => End - Start < Width;
    public ulong InFlight { get; }
    public MeasurementQuality Quality { get; }
    public ImmutableArray<ulong> Counters { get; }
    public ImmutableArray<ulong> StatusReceived { get; }
    public ImmutableArray<ulong> RetryAfterKinds { get; }
    public ulong RetryAfterClamped { get; }
    public ulong RetryAfterClockUncertain { get; }
    public TimeSpan? MinimumRetryAfter { get; }
    public TimeSpan? MaximumRetryAfter { get; }
    public ulong Registrations { get; }
    public ulong RegistrationDelaySumNanoseconds { get; }
    public ulong RegistrationDelayMaxNanoseconds { get; }
    public HistogramSnapshot TerminalDuration { get; }
    public HistogramSnapshot HttpCompletedDuration { get; }
    public ulong this[MetricCounter counter] => Counters[(int)counter];
    public ulong FinalStatuses => StatusReceived.Aggregate(0UL, (a, b) => checked(a + b));
    public ulong OtherFinalStatuses => checked(FinalStatuses - StatusReceived[429 - 100]);
    public double? RateLimitedPercent => FinalStatuses == 0 ? null : 100.0 * StatusReceived[329] / FinalStatuses;
}

/// <summary>Manifest exists only after final close; absence and saturation cannot be interpreted as zero.</summary>
public readonly record struct MetricWindowManifest(ulong LastWindowIndex, TimeSpan End, bool Complete,
    MeasurementQuality Quality);

public sealed class MetricBackpressureException : Exception
{
    public MetricBackpressureException() : base("Metric window sink capacity exhausted.") { }
}

/// <summary>Bounded nonblocking local handoff. No external callbacks or I/O under collector locks.</summary>
public sealed class MetricWindowSink
{
    public const int MaximumCapacity = 6;
    public const int ReservedBytesPerWindow = 40 * 1024;
    public const int MaximumReservedBytes = MaximumCapacity * ReservedBytesPerWindow;
    private readonly object gate = new();
    private readonly Queue<MetricWindow> queue;
    private TaskCompletionSource readable = NewSignal();
    private bool complete;
    public MetricWindowSink(int capacity = MaximumCapacity)
    {
        if (capacity is < 1 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        queue = new(capacity);
    }
    public int Capacity { get; }
    public int Count { get { lock (gate) return queue.Count; } }
    internal bool TryWrite(MetricWindow window)
    {
        lock (gate)
        {
            if (complete || queue.Count == Capacity) return false;
            queue.Enqueue(window);
            readable.TrySetResult();
            return true;
        }
    }
    public bool TryRead(out MetricWindow? window)
    {
        lock (gate)
        {
            bool read = queue.TryDequeue(out window);
            ResetSignalIfEmpty();
            return read;
        }
    }

    // The lifecycle has a single consumer and acknowledges only after output confirms the write.
    internal async ValueTask<MetricWindow?> PeekAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (queue.TryPeek(out var window)) return window;
                if (complete) return null;
                signal = readable.Task;
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    internal void Acknowledge(MetricWindow window)
    {
        lock (gate)
        {
            if (!queue.TryPeek(out var head) || !ReferenceEquals(head, window))
                throw new InvalidOperationException("Metric acknowledgement is out of order.");
            queue.Dequeue();
            ResetSignalIfEmpty();
        }
    }
    internal ImmutableArray<MetricWindow> Snapshot() { lock (gate) return ImmutableArray.CreateRange(queue); }
    internal void Complete() { lock (gate) { complete = true; readable.TrySetResult(); } }
    private void ResetSignalIfEmpty()
    {
        if (queue.Count == 0 && !complete && readable.Task.IsCompleted) readable = NewSignal();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
