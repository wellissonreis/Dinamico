using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;

namespace Dinamico.Web.Tests;

public sealed class MetricProjectionTests
{
    [Theory]
    [InlineData(NotStartedReason.SchedulerLate)]
    [InlineData(NotStartedReason.ConcurrencyLimit)]
    [InlineData(NotStartedReason.Policy)]
    public void Half_not_started_is_reported_as_generator_drops_rather_than_target_errors(NotStartedReason reason)
    {
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(new Clock(), 0, sink);
        collector.RecordNotStarted(reason, 50, 0);
        for (int i = 0; i < 50; i++)
        {
            collector.RecordStarted(0);
            collector.RecordHeaders(new(i, 200, default, 0));
            collector.RecordTerminal(new(i, RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(10), 200, 0), 0);
        }
        var manifest = collector.Close();
        Assert.True(sink.TryRead(out var window));
        var totals = MetricWindowTotals.Empty.Append(window!);
        var view = Views.Metrics(100, totals);

        Assert.Equal(100UL, view.Planned);
        Assert.Equal(100UL, view.Scheduled);
        Assert.Equal(50UL, view.Started);
        Assert.Equal(50UL, view.HttpCompleted);
        Assert.Equal(50UL, view.HttpSuccess);
        Assert.Equal(50UL, view.Dropped);
        Assert.Equal(0UL, view.Errors);
        Assert.Equal(0UL, view.InFlight);
        Assert.Equal(view.Scheduled, view.Started + view.Dropped);
        Assert.True(totals.HasCompleteCoverage(manifest));
        Assert.Contains("GeneratorLimited", Views.Quality(totals.Quality));
        Assert.Equal(view.Dropped, Views.Window(window!).Dropped);
    }

    [Fact]
    public void Timeout_after_headers_is_an_error_and_has_no_completed_http_latency()
    {
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(new Clock(), 0, sink);
        collector.RecordStarted(0);
        collector.RecordHeaders(new(0, 200, default, 0));
        collector.RecordTerminal(new(0, RequestOutcome.RequestTimedOut, TimeSpan.FromSeconds(2),
            200, 0, RequestErrorKind.RequestTimeout), 0);
        collector.Close();
        Assert.True(sink.TryRead(out var window));
        var view = Views.Metrics(1, MetricWindowTotals.Empty.Append(window!));

        Assert.Equal(1UL, view.Started);
        Assert.Equal(1UL, view.Errors);
        Assert.Equal(0UL, view.Dropped);
        Assert.Equal(0UL, view.HttpCompleted);
        Assert.Equal(0UL, view.HttpSuccess);
        Assert.Null(view.P50Milliseconds);
        Assert.Null(view.P95Milliseconds);
        Assert.Null(view.P99Milliseconds);
        Assert.Null(Views.Window(window!).P95Milliseconds);
        Assert.Equal(1UL, window!.TerminalDuration.Count);
        Assert.Equal(2_000_000_000UL, window.TerminalDuration.SumNanoseconds);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public void Http_rejection_is_counted_once_as_completed_error_and_never_as_drop(int status)
    {
        var sink = new MetricWindowSink();
        var collector = new MetricWindowCollector(new Clock(), 0, sink);
        collector.RecordStarted(0);
        collector.RecordHeaders(new(0, status, default, 0));
        collector.RecordTerminal(new(0, RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(25), status, 0), 0);
        collector.Close();
        Assert.True(sink.TryRead(out var window));
        var view = Views.Metrics(1, MetricWindowTotals.Empty.Append(window!));

        Assert.Equal(1UL, view.Errors);
        Assert.Equal(1UL, view.HttpCompleted);
        Assert.Equal(0UL, view.HttpSuccess);
        Assert.Equal(0UL, view.Dropped);
        Assert.Equal(status == 429 ? 100d : 0d, view.RateLimitedPercent);
        Assert.InRange(view.P95Milliseconds!.Value, 25, 26);
        Assert.Equal(view.Errors, Views.Window(window!).Errors);
    }

    private sealed class Clock : IMonotonicClock
    {
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    }
}
