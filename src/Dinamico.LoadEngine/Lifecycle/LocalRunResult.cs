using System.Collections.Immutable;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Scheduling;

namespace Dinamico.LoadEngine.Lifecycle;

public enum LocalRunState { Prepared, Generating, Draining, Stopping, Finished }
public enum LocalRunReason { DurationElapsed, UserCancelled, TelemetryBackpressure, InternalFailure, OutputFailure, FlushTimedOut, DrainTimedOut }
public enum LocalRunCompletion { Completed, Cancelled, Failed }
[Flags]
public enum LocalRunFailure { None = 0, TelemetryBackpressure = 1, Internal = 2, Output = 4, FlushTimeout = 8, DrainTimeout = 16 }
public readonly record struct LocalRunTransition(LocalRunState State, TimeSpan Elapsed);
public sealed record SchedulerDiagnostics(long WakeCount, long LimitedWakeCount,
    long ToleranceExceededDrops, long WakeCapacityDrops, long EndBoundaryDrops,
    TimeSpan MaxObservedLateness, TimeSpan Quantum, TimeSpan LatenessTolerance);

/// <summary>Output owns no transport. Calls must honor cancellation; WriteAsync confirms one immutable window.</summary>
public interface ILocalMetricOutput
{
    ValueTask WriteAsync(MetricWindow window, CancellationToken cancellationToken);
    ValueTask FlushAsync(MetricWindowManifest? manifest, CancellationToken cancellationToken);
}

public interface ILocalDiagnosticOutput
{
    void UpdateDiagnostics(Observability.RunDiagnostics diagnostics);
}

/// <summary>Safe summary: no target URL, headers, payload, arbitrary exception text or per-request history.</summary>
public sealed record LocalRunResult(
    Guid ExecutionId,
    LocalRunCompletion Completion,
    LocalRunReason Reason,
    LocalRunFailure Failures,
    long PlannedTotal,
    long ScheduledDue,
    long NotScheduledAfterStop,
    TimeSpan GenerationDuration,
    TimeSpan DrainDuration,
    TimeSpan FlushDuration,
    TimeSpan TotalDuration,
    AdmissionSnapshot Admission,
    MetricWindowTotals Totals,
    MetricWindowManifest? Manifest,
    MeasurementQuality Quality,
    bool OperationsDrained,
    bool MetricsCoverageComplete,
    bool FlushConfirmed,
    bool OutputStopped,
    ImmutableArray<LocalRunTransition> Transitions)
{
    public bool DataComplete => OperationsDrained && MetricsCoverageComplete && FlushConfirmed && OutputStopped;
    public SchedulerDiagnostics? Scheduler { get; init; }
    public Observability.RunDiagnostics? Diagnostics { get; init; }
}
