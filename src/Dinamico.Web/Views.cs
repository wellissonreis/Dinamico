using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;
using Dinamico.LoadEngine.Observability;

namespace Dinamico.Web;

public sealed record StartRequest(string? Url, string? Method, long RequestsPerSecond, long DurationSeconds,
    int MaxConcurrency, double RequestTimeoutSeconds, string? RateLimitMode, HttpHeader[]? Headers,
    string? Payload, int? TestCredentialHeaderIndex, bool TestAuthorizationDeclared)
{
    public string CaptureMode { get; init; } = "Off";
    public bool CorrelateRequests { get; init; }
    public override string ToString() => nameof(StartRequest);
}
public sealed record PlanView(long RequestsPerSecond, long DurationSeconds, int MaxConcurrency, string Method, string RateLimitMode);
public sealed record MetricsView(ulong Planned, ulong Scheduled, ulong Started, ulong HttpCompleted, ulong HttpSuccess,
    ulong Errors, ulong Dropped, ulong InFlight, double? RateLimitedPercent, double? P50Milliseconds, double? P95Milliseconds, double? P99Milliseconds)
{
    public BreakdownView? Breakdown { get; init; }
    public ulong HttpCount { get; init; }
    public ulong TerminalCount { get; init; }
    public double? MinMilliseconds { get; init; }
    public double? MaxMilliseconds { get; init; }
    public double? TerminalP95Milliseconds { get; init; }
}
public sealed record BreakdownView(ulong SchedulerLate, ulong ConcurrencyLimit, ulong Policy,
    ulong TransportFailed, ulong RequestTimedOut, ulong CancelledInFlight, ulong ResponseLimitExceeded,
    ulong InternalFailed, Dictionary<string, ulong> Statuses);
public sealed record WindowView(ulong Index, double StartSeconds, double EndSeconds, ulong Started, ulong HttpCompleted, ulong Errors, ulong Dropped, double? P95Milliseconds)
{
    public BreakdownView? Breakdown { get; init; }
    public ulong HttpSuccess { get; init; }
    public ulong InFlight { get; init; }
    public ulong HttpCount { get; init; }
    public ulong TerminalCount { get; init; }
    public double? P50Milliseconds { get; init; }
    public double? P99Milliseconds { get; init; }
    public double? MinMilliseconds { get; init; }
    public double? MaxMilliseconds { get; init; }
    public double? TerminalP95Milliseconds { get; init; }
    public string[] Quality { get; init; } = [];
}
public sealed record ExecutionView(Guid ExecutionId, Guid ProjectId, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc,
    string State, string Persistence, bool DataComplete, string[] Quality, string? Reason, PlanView? Plan,
    MetricsView Metrics, WindowView[] Windows, double ElapsedSeconds)
{
    public ulong? NotScheduledAfterStop { get; init; }
    public RunDiagnostics? Diagnostics { get; init; }
}

internal static class Views
{
    internal static double? Milliseconds(HistogramQuantile? value) => value?.UpperBoundNanoseconds is { } ns ? ns / 1_000_000d : null;
    internal static string[] Quality(MeasurementQuality quality) => quality == MeasurementQuality.None ? [] : quality.ToString().Split(", ");
    internal static ulong Errors(IReadOnlyList<ulong> c) => c[(int)MetricCounter.HttpCompleted] - c[(int)MetricCounter.HttpSuccess]
        + c[(int)MetricCounter.TransportFailed] + c[(int)MetricCounter.RequestTimedOut]
        + c[(int)MetricCounter.CancelledInFlight] + c[(int)MetricCounter.ResponseLimitExceeded] + c[(int)MetricCounter.InternalFailed];
    internal static ulong Dropped(IReadOnlyList<ulong> c) => c[(int)MetricCounter.SchedulerLate] + c[(int)MetricCounter.ConcurrencyLimit] + c[(int)MetricCounter.Policy];
    internal static BreakdownView Breakdown(IReadOnlyList<ulong> c, IReadOnlyList<ulong> statuses) =>
        new(c[2], c[3], c[4], c[7], c[8], c[9], c[10], c[11], statuses.Select((count, index) => (count, index))
            .Where(p => p.count != 0).ToDictionary(p => p.index == 500 ? "other" : (p.index + 100).ToString(System.Globalization.CultureInfo.InvariantCulture), p => p.count));
    internal static WindowView Window(MetricWindow w) => new(w.WindowIndex, w.Start.TotalSeconds, w.End.TotalSeconds,
        w[MetricCounter.Started], w[MetricCounter.HttpCompleted], Errors(w.Counters), Dropped(w.Counters), Milliseconds(w.HttpCompletedDuration.P95))
        {
            Breakdown = Breakdown(w.Counters, w.StatusReceived), HttpSuccess = w[MetricCounter.HttpSuccess], InFlight = w.InFlight,
            HttpCount = w.HttpCompletedDuration.Count, TerminalCount = w.TerminalDuration.Count,
            P50Milliseconds = Milliseconds(w.HttpCompletedDuration.P50), P99Milliseconds = Milliseconds(w.HttpCompletedDuration.P99),
            MinMilliseconds = w.HttpCompletedDuration.MinimumNanoseconds / 1_000_000d,
            MaxMilliseconds = w.HttpCompletedDuration.MaximumNanoseconds / 1_000_000d,
            TerminalP95Milliseconds = Milliseconds(w.TerminalDuration.P95), Quality = Quality(w.Quality)
        };
    internal static MetricsView Metrics(ulong planned, MetricWindowTotals t) => new(planned, t[MetricCounter.ScheduledDue], t[MetricCounter.Started],
        t[MetricCounter.HttpCompleted], t[MetricCounter.HttpSuccess], Errors(t.Counters), Dropped(t.Counters), t.LastInFlight,
        t.RateLimitedPercent, Milliseconds(t.HttpCompletedDuration.P50), Milliseconds(t.HttpCompletedDuration.P95), Milliseconds(t.HttpCompletedDuration.P99))
        {
            Breakdown = Breakdown(t.Counters, t.StatusReceived), HttpCount = t.HttpCompletedDuration.Count, TerminalCount = t.TerminalDuration.Count,
            MinMilliseconds = t.HttpCompletedDuration.MinimumNanoseconds / 1_000_000d,
            MaxMilliseconds = t.HttpCompletedDuration.MaximumNanoseconds / 1_000_000d,
            TerminalP95Milliseconds = Milliseconds(t.TerminalDuration.P95)
        };
    internal static ExecutionView Stored(StoredExecution e, IReadOnlyList<MetricWindow> windows)
    {
        var s = e.Summary;
        var c = s?.Counters ?? new ulong[MetricWindow.CounterCount];
        ulong statuses = s?.StatusReceived.Aggregate(0UL, (a, b) => a + b) ?? 0;
        return new(e.ExecutionId, e.ProjectId, e.StartedUtc, e.FinishedUtc, e.Completion.ToString(), "saved", e.DataComplete,
            Quality(e.Quality), e.Reason.ToString(), e.Snapshot is { } p ? new(p.RequestsPerSecond, p.DurationSeconds, p.MaxConcurrency, p.Method, p.Mode.ToString()) : null,
            new(e.PlannedTotal, e.ScheduledDue, c[1], c[5], c[6], Errors(c), Dropped(c), s?.LastInFlight ?? 0,
                statuses == 0 ? null : 100d * s!.StatusReceived[329] / statuses,
                Milliseconds(s?.HttpCompletedDuration.Quantile(50)), Milliseconds(s?.HttpCompletedDuration.Quantile(95)), Milliseconds(s?.HttpCompletedDuration.Quantile(99)))
            {
                Breakdown = Breakdown(c, s?.StatusReceived ?? new ulong[MetricWindow.StatusCount]),
                HttpCount = s?.HttpCompletedDuration.Count ?? 0, TerminalCount = s?.TerminalDuration.Count ?? 0,
                MinMilliseconds = s?.HttpCompletedDuration.MinimumNanoseconds / 1_000_000d,
                MaxMilliseconds = s?.HttpCompletedDuration.MaximumNanoseconds / 1_000_000d,
                TerminalP95Milliseconds = Milliseconds(s?.TerminalDuration.Quantile(95))
            },
            windows.Select(Window).ToArray(), (e.FinishedUtc - e.StartedUtc).TotalSeconds)
            { NotScheduledAfterStop = e.NotScheduledAfterStop, Diagnostics = e.Diagnostics };
    }
}
