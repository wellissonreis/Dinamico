using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Timing;
using Dinamico.LoadEngine.Transport;

namespace Dinamico.LoadEngine.Scheduling;

/// <summary>Nonblocking admission, no pending queue or retained completed tasks.
/// The observer must be fast and thread-safe. Stop admissions before draining.</summary>
public sealed class BoundedAdmission
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Func<long, CancellationToken, Task<RequestObservation>> _send;
    private readonly Action<RequestObservation> _observe;
    private readonly IMonotonicClock _clock;
    private TaskCompletionSource _idle = CompletedSource();
    private Exception? _observerFailure;
    private bool _stopped;
    private int _inFlight;
    private int _peak;
    private long _started, _completed, _concurrencyDrops, _policyDrops;

    public BoundedAdmission(int capacity,
        Func<long, CancellationToken, Task<RequestObservation>> send,
        Action<RequestObservation> observe, IMonotonicClock? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _clock = clock ?? StopwatchMonotonicClock.Instance;
    }

    public BoundedAdmission(LoadTestPlan plan, LocalHttpTransport transport,
        Action<RequestObservation> observe)
        : this((plan ?? throw new ArgumentNullException(nameof(plan))).MaxConcurrency,
            (transport ?? throw new ArgumentNullException(nameof(transport))).SendAsync, observe) { }

    public AdmissionSnapshot Snapshot
    {
        get { lock (_gate) return new(_started, _completed, _inFlight, _peak,
            _concurrencyDrops, _policyDrops); }
    }

    public bool TryStart(ArrivalIntent arrival, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(arrival.Index);
        lock (_gate)
        {
            if (_stopped || cancellationToken.IsCancellationRequested)
            {
                _policyDrops = checked(_policyDrops + 1);
                return false;
            }
            if (_inFlight == _capacity)
            {
                _concurrencyDrops = checked(_concurrencyDrops + 1);
                return false;
            }
            _started = checked(_started + 1);
            if (_inFlight++ == 0)
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _peak = Math.Max(_peak, _inFlight);
        }
        // RunAsync observes every exception; no Task.Run and no history list.
        _ = RunAsync(arrival.Index, cancellationToken);
        return true;
    }

    public void StopAdmissions() { lock (_gate) _stopped = true; }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
        {
            if (!_stopped) throw new InvalidOperationException("Stop admissions before draining.");
            idle = _idle.Task;
        }
        await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            if (_observerFailure is not null)
                throw new InvalidOperationException("Terminal observer failed.", _observerFailure);
    }

    private async Task RunAsync(long index, CancellationToken cancellationToken)
    {
        long start = 0;
        RequestObservation observation;
        try
        {
            start = _clock.GetTimestamp();
            observation = await _send(index, cancellationToken).ConfigureAwait(false);
            if (observation.ArrivalIndex != index || observation.Outcome == RequestOutcome.Unknown)
                throw new InvalidOperationException("Invalid transport observation.");
        }
        catch (Exception)
        {
            TimeSpan elapsed;
            try { elapsed = _clock.GetElapsedTime(start, _clock.GetTimestamp()); }
            catch (Exception) { elapsed = TimeSpan.Zero; }
            observation = new(index, RequestOutcome.InternalFailed,
                elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed, null, 0, RequestErrorKind.Internal);
        }
        try { _observe(observation); }
        catch (Exception ex) { lock (_gate) { _observerFailure ??= ex; _stopped = true; } }
        finally
        {
            lock (_gate)
            {
                _completed++;
                if (--_inFlight == 0) _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

public readonly record struct AdmissionSnapshot(long Started, long Completed, int InFlight,
    int PeakInFlight, long ConcurrencyLimit, long Policy)
{
    public bool GeneratorLimited => ConcurrencyLimit > 0;
}
