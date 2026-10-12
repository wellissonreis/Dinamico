namespace Dinamico.LoadEngine.Contracts;

public enum RequestOutcome
{
    Unknown = 0,
    HttpCompleted,
    TransportFailed,
    RequestTimedOut,
    CancelledInFlight,
    ResponseLimitExceeded,
    InternalFailed
}

public enum RequestErrorKind
{
    None = 0,
    Dns,
    Connect,
    Tls,
    Protocol,
    ResponseRead,
    RequestTimeout,
    Cancellation,
    ResponseLimit,
    Internal
}

public enum NotStartedReason
{
    SchedulerLate = 1,
    ConcurrencyLimit,
    Policy
}

[Flags]
public enum MeasurementQuality
{
    None = 0,
    GeneratorLimited = 1,
    ClockUncertain = 2,
    MetricsPartial = 4,
    WorkerLost = 8,
    TargetErrors = 16
}

/// <summary>Observação transitória, não uma entidade persistida por request. default tem Outcome Unknown.</summary>
public readonly record struct RequestObservation
{
    public RequestObservation(long arrivalIndex, RequestOutcome outcome, TimeSpan terminalDuration,
        int? statusCode, long responseBodyBytes, RequestErrorKind errorKind = RequestErrorKind.None,
        RetryAfterObservation retryAfter = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(arrivalIndex);
        if (terminalDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(terminalDuration));
        ArgumentOutOfRangeException.ThrowIfNegative(responseBodyBytes);
        if (statusCode is < 200 or > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        if ((statusCode != 429 && retryAfter.Kind is not (RetryAfterKind.NotObserved or RetryAfterKind.NotApplicable))
            || (statusCode == 429 && retryAfter.Kind == RetryAfterKind.NotApplicable))
            throw new ArgumentException("Retry-After evidence requires HTTP 429.", nameof(retryAfter));

        bool valid = outcome switch
        {
            RequestOutcome.HttpCompleted => statusCode.HasValue && errorKind == RequestErrorKind.None,
            RequestOutcome.TransportFailed => errorKind is RequestErrorKind.Dns or RequestErrorKind.Connect
                or RequestErrorKind.Tls or RequestErrorKind.Protocol or RequestErrorKind.ResponseRead,
            RequestOutcome.RequestTimedOut => errorKind == RequestErrorKind.RequestTimeout,
            RequestOutcome.CancelledInFlight => errorKind == RequestErrorKind.Cancellation,
            RequestOutcome.ResponseLimitExceeded => errorKind == RequestErrorKind.ResponseLimit,
            RequestOutcome.InternalFailed => errorKind == RequestErrorKind.Internal,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("Inconsistent terminal outcome and error kind.", nameof(outcome));

        ArrivalIndex = arrivalIndex;
        Outcome = outcome;
        TerminalDuration = terminalDuration;
        StatusCode = statusCode;
        ResponseBodyBytes = responseBodyBytes;
        ErrorKind = errorKind;
        RetryAfter = retryAfter;
    }

    public long ArrivalIndex { get; }
    public RequestOutcome Outcome { get; }
    public TimeSpan TerminalDuration { get; }
    public int? StatusCode { get; }
    public long ResponseBodyBytes { get; }
    public RequestErrorKind ErrorKind { get; }
    public RetryAfterObservation RetryAfter { get; }
    public bool IsRateLimited => StatusCode == 429;
    public bool IsAccepted(HttpSuccessRule rule) => rule switch
    {
        HttpSuccessRule.Completed2xxV1 => Outcome == RequestOutcome.HttpCompleted && StatusCode is >= 200 and <= 299,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), "Unsupported HTTP success rule.")
    };
    public TimeSpan? HttpCompletedDuration => Outcome == RequestOutcome.HttpCompleted ? TerminalDuration : null;
}
