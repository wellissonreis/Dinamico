using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Lifecycle;

namespace Dinamico.Persistence;

public sealed record LocalRunSnapshot(int RequestsPerSecond, int DurationSeconds, int MaxConcurrency,
    RateLimitMode Mode = RateLimitMode.Normal, LoadRetryPolicy RetryPolicy = LoadRetryPolicy.None,
    HttpSuccessRule SuccessRule = HttpSuccessRule.Completed2xxV1, string Method = "GET",
    long RequestTimeoutTicks = 100000000, int MaxResponseBytes = 1048576,
    string TargetScheme = "http", int TargetPort = 80);
public sealed record ExecutionImport(Guid ProjectId, Guid ExecutionId, DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc, LocalRunSnapshot Snapshot, IReadOnlyList<MetricWindow> Windows,
    bool DataComplete, ulong PlannedTotal, ulong ScheduledDue, ulong NotScheduledAfterStop,
    MetricWindowManifest? Manifest = null, LocalRunCompletion Completion = LocalRunCompletion.Completed,
    LocalRunReason Reason = LocalRunReason.DurationElapsed, LocalRunFailure Failures = LocalRunFailure.None, MeasurementQuality Quality = MeasurementQuality.None)
{
    public RunDiagnostics? Diagnostics { get; init; }
}
public enum ImportResult { Inserted, Replayed }
public sealed class ImportConflictException : Exception
{
    public ImportConflictException() : base("Execution identity already has different canonical content.") { }
}
public sealed record ExecutionCursor(DateTimeOffset CreatedUtc, Guid ExecutionId);
public sealed record ExecutionPage(IReadOnlyList<StoredExecution> Items, ExecutionCursor? NextCursor);
public sealed record PruneResult(int DetailsRemoved, int SummariesRemoved, int IdentitiesRemoved);
public sealed record StorageMeasurement(long RelationBytes, decimal? WalPositionBytes);
public sealed record StoredExecution(Guid ProjectId, Guid ExecutionId, DateTimeOffset CreatedUtc,
    DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, LocalRunSnapshot? Snapshot,
    bool DataComplete, ulong PlannedTotal, ulong ScheduledDue, ulong NotScheduledAfterStop,
    bool DetailRetained, RunSummary? Summary, LocalRunCompletion Completion, LocalRunReason Reason, LocalRunFailure Failures, MeasurementQuality Quality,
    int SnapshotSchemaVersion, string SnapshotHash, string ContentHash)
{
    public RunDiagnostics? Diagnostics { get; init; }
}
public sealed record HistogramSummary(ulong Count, ulong SumNanoseconds, ulong? MinimumNanoseconds,
    ulong? MaximumNanoseconds, ulong OverflowCount, ulong[] Counts)
{
    public HistogramQuantile? Quantile(int percent)
    {
        if (percent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        if (Count == 0) return null;
        ulong rank = (ulong)(((UInt128)(uint)percent * Count + 99) / 100), cumulative = 0;
        for (int i = 0; i < Counts.Length; i++)
        {
            cumulative = checked(cumulative + Counts[i]);
            if (cumulative >= rank) return new(Count, i == Counts.Length - 1 ? null : LogHistogramSchema.UpperBounds[i], i == Counts.Length - 1);
        }
        throw new InvalidDataException("Invalid histogram population.");
    }
    internal static HistogramSummary From(HistogramSnapshot value) => new(value.Count, value.SumNanoseconds,
        value.MinimumNanoseconds, value.MaximumNanoseconds, value.OverflowCount, value.Counts.ToArray());
}
public sealed record RunSummary(ulong WindowCount, long EndTicks, ulong LastInFlight, MeasurementQuality Quality,
    ulong[] Counters, ulong[] StatusReceived, ulong[] RetryAfterKinds, ulong Registrations,
    ulong RegistrationDelaySumNanoseconds, ulong RegistrationDelayMaxNanoseconds,
    ulong RetryAfterClamped, ulong RetryAfterClockUncertain, long? MinimumRetryAfterTicks,
    long? MaximumRetryAfterTicks, HistogramSummary TerminalDuration, HistogramSummary HttpCompletedDuration)
{
    public MetricWindowManifest? Manifest { get; init; }
    internal static RunSummary From(MetricWindowTotals value) => new(value.WindowCount, value.End.Ticks,
        value.LastInFlight, value.Quality, value.Counters.ToArray(), value.StatusReceived.ToArray(),
        value.RetryAfterKinds.ToArray(), value.Registrations, value.RegistrationDelaySumNanoseconds,
        value.RegistrationDelayMaxNanoseconds, value.RetryAfterClamped, value.RetryAfterClockUncertain,
        value.MinimumRetryAfter?.Ticks, value.MaximumRetryAfter?.Ticks,
        HistogramSummary.From(value.TerminalDuration), HistogramSummary.From(value.HttpCompletedDuration));
}
