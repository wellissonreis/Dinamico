using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Timing;

/// <summary>One terminal latch shared by the timer, caller cancellation and observed I/O.</summary>
internal sealed class OperationDeadline : IDisposable
{
    private readonly object _gate = new();
    private readonly IMonotonicClock _clock;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _cancellation;
    private readonly ITimer? _timer;
    private CancellationTokenRegistration _registration;
    private bool _disposed;
    private bool _cancelling;
    private RequestOutcome _outcome;
    private RequestErrorKind _error;
    private TimeSpan _duration;

    internal OperationDeadline(IMonotonicClock clock, TimeProvider timeProvider,
        TimeSpan timeout, CancellationToken externalCancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeout.Ticks);
        _clock = clock;
        _timeout = timeout;
        StartedTimestamp = clock.GetTimestamp();
        _cancellation = new CancellationTokenSource();
        try
        {
            _timer = timeProvider.CreateTimer(static state => ((OperationDeadline)state!).OnTimer(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _registration = externalCancellation.UnsafeRegister(static state =>
                ((OperationDeadline)state!).CancelExternally(), this);
            OnTimer();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal CancellationToken Token => _cancellation.Token;
    internal long StartedTimestamp { get; }
    internal long? HeadersTimestamp { get; private set; }
    internal long? EndedTimestamp { get; private set; }

    internal void MarkHeaders()
    {
        lock (_gate)
            HeadersTimestamp ??= _clock.GetTimestamp();
    }

    internal (RequestOutcome Outcome, RequestErrorKind Error, TimeSpan Duration) Result
    {
        get
        {
            lock (_gate)
                return (_outcome, _error, _duration);
        }
    }

    internal void Complete(RequestOutcome outcome, RequestErrorKind error)
    {
        bool cancel;
        lock (_gate)
        {
            if (_disposed || _outcome != RequestOutcome.Unknown)
                return;
            cancel = Finish(outcome, error, checkDeadline: true);
        }
        if (cancel)
            CancelSource();
    }

    private bool TryGetElapsed(out long ended, out TimeSpan elapsed)
    {
        ended = _clock.GetTimestamp();
        try
        {
            elapsed = _clock.GetElapsedTime(StartedTimestamp, ended);
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            elapsed = TimeSpan.Zero;
            return false;
        }
    }

    private void OnTimer()
    {
        bool cancel;
        lock (_gate)
        {
            if (_disposed || _outcome != RequestOutcome.Unknown)
                return;
            if (!TryGetElapsed(out _, out TimeSpan elapsed))
                cancel = Finish(RequestOutcome.InternalFailed, RequestErrorKind.Internal);
            else if (elapsed < _timeout)
            {
                ArmTimer(_timeout - elapsed);
                return;
            }
            else
                cancel = Finish(RequestOutcome.RequestTimedOut, RequestErrorKind.RequestTimeout);
        }
        if (cancel)
            CancelSource();
    }

    private void ArmTimer(TimeSpan remaining)
    {
        // System timers have millisecond resolution. Round up to avoid repeated early wakeups.
        long milliseconds = checked((remaining.Ticks + TimeSpan.TicksPerMillisecond - 1)
            / TimeSpan.TicksPerMillisecond);
        _timer!.Change(TimeSpan.FromMilliseconds(milliseconds), Timeout.InfiniteTimeSpan);
    }

    private void CancelExternally()
    {
        bool cancel;
        lock (_gate)
        {
            if (_disposed || _outcome != RequestOutcome.Unknown)
                return;
            cancel = Finish(RequestOutcome.CancelledInFlight, RequestErrorKind.Cancellation);
        }
        if (cancel)
            CancelSource();
    }

    // Only the winner owns cancellation; callbacks run outside the gate and may complete I/O inline.
    private bool Finish(RequestOutcome outcome, RequestErrorKind error, bool checkDeadline = false)
    {
        if (_outcome != RequestOutcome.Unknown)
            return false;
        if (!TryGetElapsed(out long ended, out _duration))
        {
            outcome = RequestOutcome.InternalFailed;
            error = RequestErrorKind.Internal;
        }
        // A delayed timer must not allow an operation observed past its deadline to succeed.
        else if (checkDeadline && _duration >= _timeout)
        {
            outcome = RequestOutcome.RequestTimedOut;
            error = RequestErrorKind.RequestTimeout;
        }
        EndedTimestamp = ended;
        _outcome = outcome;
        _error = error;
        _cancelling = outcome is RequestOutcome.RequestTimedOut or RequestOutcome.CancelledInFlight
            or RequestOutcome.InternalFailed;
        return _cancelling;
    }

    private void CancelSource()
    {
        try
        {
            _cancellation.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                if (_disposed)
                    _cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        // Unregister outside the gate: Dispose can wait for an external callback using it.
        _registration.Dispose();
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            // An inline I/O continuation may dispose us from inside a cancellation callback.
            if (!_cancelling)
                _cancellation.Dispose();
        }
    }
}
