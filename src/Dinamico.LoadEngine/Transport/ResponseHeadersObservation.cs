using Dinamico.LoadEngine.Contracts;

namespace Dinamico.LoadEngine.Transport;

/// <summary>Transient headers event, independent of the terminal event. Timestamp uses the transport clock.</summary>
public readonly record struct ResponseHeadersObservation
{
    public ResponseHeadersObservation(long arrivalIndex, int statusCode, RetryAfterObservation retryAfter,
        long observedTimestamp)
    {
        // Reuse the status/evidence contract without accepting Unknown or unvalidated associations.
        _ = new RequestObservation(arrivalIndex, RequestOutcome.HttpCompleted, TimeSpan.Zero, statusCode, 0,
            retryAfter: retryAfter);
        ArrivalIndex = arrivalIndex;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ObservedTimestamp = observedTimestamp;
    }

    public long ArrivalIndex { get; }
    public int StatusCode { get; }
    public RetryAfterObservation RetryAfter { get; }
    public long ObservedTimestamp { get; }
}

public sealed class TransportObserverException : Exception
{
    public TransportObserverException(Exception innerException)
        : base("Transport headers observer failed.", innerException) { }
}
