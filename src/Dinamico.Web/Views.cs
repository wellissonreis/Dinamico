using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;

namespace Dinamico.Web;

public sealed record StartRequest(string? Url, string? Method, long RequestsPerSecond, long DurationSeconds,
    int MaxConcurrency, double RequestTimeoutSeconds, string? RateLimitMode, HttpHeader[]? Headers,
    string? Payload, int? TestCredentialHeaderIndex, bool TestAuthorizationDeclared)
{
    public override string ToString() => nameof(StartRequest);
}
public sealed record PlanView(long RequestsPerSecond, long DurationSeconds, int MaxConcurrency, string Method, string RateLimitMode);
public sealed record MetricsView(ulong Planned, ulong Scheduled, ulong Started, ulong HttpCompleted, ulong HttpSuccess,
    ulong Errors, ulong Dropped, ulong InFlight, double? RateLimitedPercent, double? P50Milliseconds, double? P95Milliseconds, double? P99Milliseconds);
public sealed record WindowView(ulong Index, double StartSeconds, double EndSeconds, ulong Started, ulong HttpCompleted, ulong Errors, ulong Dropped, double? P95Milliseconds);
public sealed record ExecutionView(Guid ExecutionId, Guid ProjectId, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc,
    string State, string Persistence, bool DataComplete, string[] Quality, string? Reason, PlanView? Plan,
    MetricsView Metrics, WindowView[] Windows, double ElapsedSeconds);

internal static class Views
{
    internal static double? Milliseconds(HistogramQuantile? value) => value?.UpperBoundNanoseconds is { } ns ? ns / 1_000_000d : null;
    internal static string[] Quality(MeasurementQuality quality) => quality == MeasurementQuality.None ? [] : quality.ToString().Split(", ");
    internal static ulong Errors(IReadOnlyList<ulong> c) => c[(int)MetricCounter.HttpCompleted] - c[(int)MetricCounter.HttpSuccess]
        + c[(int)MetricCounter.TransportFailed] + c[(int)MetricCounter.RequestTimedOut]
        + c[(int)MetricCounter.CancelledInFlight] + c[(int)MetricCounter.ResponseLimitExceeded] + c[(int)MetricCounter.InternalFailed];
    internal static ulong Dropped(IReadOnlyList<ulong> c) => c[(int)MetricCounter.SchedulerLate] + c[(int)MetricCounter.ConcurrencyLimit] + c[(int)MetricCounter.Policy];
    internal static WindowView Window(MetricWindow w) => new(w.WindowIndex, w.Start.TotalSeconds, w.End.TotalSeconds,
        w[MetricCounter.Started], w[MetricCounter.HttpCompleted], Errors(w.Counters), Dropped(w.Counters), Milliseconds(w.HttpCompletedDuration.P95));
    internal static MetricsView Metrics(ulong planned, MetricWindowTotals t) => new(planned, t[MetricCounter.ScheduledDue], t[MetricCounter.Started],
        t[MetricCounter.HttpCompleted], t[MetricCounter.HttpSuccess], Errors(t.Counters), Dropped(t.Counters), t.LastInFlight,
        t.RateLimitedPercent, Milliseconds(t.HttpCompletedDuration.P50), Milliseconds(t.HttpCompletedDuration.P95), Milliseconds(t.HttpCompletedDuration.P99));
    internal static ExecutionView Stored(StoredExecution e, IReadOnlyList<MetricWindow> windows)
    {
        var s = e.Summary;
        var c = s?.Counters ?? new ulong[MetricWindow.CounterCount];
        ulong statuses = s?.StatusReceived.Aggregate(0UL, (a, b) => a + b) ?? 0;
        return new(e.ExecutionId, e.ProjectId, e.StartedUtc, e.FinishedUtc, e.Completion.ToString(), "saved", e.DataComplete,
            Quality(e.Quality), e.Reason.ToString(), e.Snapshot is { } p ? new(p.RequestsPerSecond, p.DurationSeconds, p.MaxConcurrency, p.Method, p.Mode.ToString()) : null,
            new(e.PlannedTotal, e.ScheduledDue, c[1], c[5], c[6], Errors(c), Dropped(c), s?.LastInFlight ?? 0,
                statuses == 0 ? null : 100d * s!.StatusReceived[329] / statuses,
                Milliseconds(s?.HttpCompletedDuration.Quantile(50)), Milliseconds(s?.HttpCompletedDuration.Quantile(95)), Milliseconds(s?.HttpCompletedDuration.Quantile(99))),
            windows.Select(Window).ToArray(), (e.FinishedUtc - e.StartedUtc).TotalSeconds);
    }
}
