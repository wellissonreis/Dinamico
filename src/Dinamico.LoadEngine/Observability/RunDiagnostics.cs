using Dinamico.LoadEngine.Lifecycle;

namespace Dinamico.LoadEngine.Observability;

public sealed record ResourceSample(ulong WindowIndex, double ElapsedSeconds,
    long? WorkingSetBytes, long? ManagedHeapBytes, double? CpuPercentOfOneCore,
    long? TotalAllocatedBytes, long? GcPauseTicks, int? Gen0, int? Gen1, int? Gen2,
    long ThreadPoolPending, int ThreadPoolThreads, long? HttpOpenConnections,
    double? HttpQueueMaxMilliseconds);

public sealed record RequestSample(long ArrivalIndex, double StartSeconds, double DurationMilliseconds,
    double? HeadersMilliseconds, int? StatusCode, string Outcome, string ErrorKind,
    string? TraceId, string? SpanId);

/// <summary>Safe, bounded diagnostic detail. No target, headers, payload or exception text.</summary>
public sealed record RunDiagnostics(int SchemaVersion, string CaptureMode, bool CorrelationEnabled,
    long Observed, long Retained, long Discarded, bool CaptureComplete,
    ResourceSample[] Resources, RequestSample[] Requests, SchedulerDiagnostics? Scheduler,
    string ExportState = "disabled", string? TraceUrlTemplate = null)
{
    public const int MaximumRequests = 2000;
    public const int MaximumResources = 133;
}
