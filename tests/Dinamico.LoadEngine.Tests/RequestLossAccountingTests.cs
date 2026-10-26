using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Scheduling;

namespace Dinamico.LoadEngine.Tests;

public sealed class RequestLossAccountingTests
{
    [Theory]
    [InlineData(1, 1000, 0)]
    [InlineData(4, 500, 500)]
    public async Task Wake_frequency_explains_missing_starts_even_with_fast_successful_transport(
        int wakeMilliseconds, long expectedStarted, long expectedLate)
    {
        var clock = new Clock();
        var sink = new MetricWindowSink();
        var metrics = new MetricWindowCollector(clock, 0, sink);
        var schedule = new ArrivalSchedule(1000, 1);
        var scheduler = new ArrivalScheduler(schedule, clock, 0);
        var sent = new HashSet<long>();
        var admission = new BoundedAdmission(1, (index, _) =>
        {
            Assert.True(sent.Add(index), "A discarded arrival must never be retried.");
            metrics.RecordStarted(clock.Ticks);
            metrics.RecordHeaders(new(index, 200, default, clock.Ticks));
            return Task.FromResult(new RequestObservation(index, RequestOutcome.HttpCompleted,
                TimeSpan.FromMilliseconds(1), 200, 0));
        }, observation => metrics.RecordTerminal(observation, clock.Ticks), clock);

        // Wake on the final due of each batch, before the generation boundary.
        // The default 1 ms quantum permits at most two arrivals per wake at 1000 RPS.
        for (clock.Ticks = TimeSpan.FromMilliseconds(wakeMilliseconds - 0.5).Ticks;
            clock.Ticks < schedule.Duration.Ticks;
            clock.Ticks += TimeSpan.FromMilliseconds(wakeMilliseconds).Ticks)
        {
            var wake = scheduler.Poll();
            metrics.RecordNotStarted(NotStartedReason.SchedulerLate, (ulong)wake.SchedulerLate, clock.Ticks);
            for (long offset = 0; offset < wake.ArrivalCount; offset++)
                Assert.True(admission.TryStart(wake[offset]));
        }
        clock.Ticks = schedule.Duration.Ticks;
        Assert.True(scheduler.Poll().IsCompleted);
        admission.StopAdmissions();
        await admission.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var manifest = metrics.Close();
        Assert.True(sink.TryRead(out var window));
        Assert.False(sink.TryRead(out _));
        var totals = MetricWindowTotals.Empty.Append(window!);

        Assert.Equal(expectedStarted, sent.Count);
        Assert.Equal(expectedStarted, admission.Snapshot.Completed);
        Assert.Equal(expectedStarted, scheduler.OfferedArrivals);
        Assert.Equal(expectedLate, scheduler.WakeCapacityDrops);
        Assert.Equal(expectedLate, scheduler.SchedulerLate);
        Assert.Equal(0, scheduler.ToleranceExceededDrops);
        Assert.Equal(0, scheduler.EndBoundaryDrops);
        Assert.Equal(1000UL, totals[MetricCounter.ScheduledDue]);
        Assert.Equal((ulong)expectedStarted, totals[MetricCounter.Started]);
        Assert.Equal((ulong)expectedStarted, totals[MetricCounter.HttpSuccess]);
        Assert.Equal((ulong)expectedLate, totals[MetricCounter.SchedulerLate]);
        Assert.Equal(0UL, totals[MetricCounter.ConcurrencyLimit]);
        Assert.Equal(totals[MetricCounter.ScheduledDue],
            totals[MetricCounter.Started] + totals[MetricCounter.SchedulerLate]);
        Assert.Equal(totals[MetricCounter.Started], totals.TerminalDuration.Count);
        Assert.Equal(0UL, totals.LastInFlight);
        Assert.True(totals.HasCompleteCoverage(manifest));
        Assert.Equal(expectedLate != 0, totals.Quality.HasFlag(MeasurementQuality.GeneratorLimited));
    }

    [Fact]
    public async Task Half_rejected_by_concurrency_never_reaches_transport_and_all_starts_finish_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<long>();
        var observed = new System.Collections.Concurrent.ConcurrentBag<long>();
        var admission = new BoundedAdmission(50, async (index, _) =>
        {
            sent.Add(index);
            await release.Task;
            return new(index, RequestOutcome.HttpCompleted, TimeSpan.FromSeconds(1), 200, 0);
        }, observation => observed.Add(observation.ArrivalIndex));
        try
        {
            for (int index = 0; index < 100; index++)
            {
                bool started = admission.TryStart(new(index, default), default, out var rejection);
                Assert.Equal(index < 50, started);
                Assert.Equal(index < 50 ? (NotStartedReason?)null : NotStartedReason.ConcurrencyLimit, rejection);
            }
            Assert.Equal(Enumerable.Range(0, 50).Select(i => (long)i), sent);
            Assert.Equal(new AdmissionSnapshot(50, 0, 50, 50, 50, 0), admission.Snapshot);
        }
        finally
        {
            admission.StopAdmissions();
            release.TrySetResult();
            await admission.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new AdmissionSnapshot(50, 50, 0, 50, 50, 0), admission.Snapshot);
        Assert.Equal(sent.Order(), observed.Order());
        Assert.Equal(50, observed.Distinct().Count());
    }

    private sealed class Clock : IMonotonicClock
    {
        public long Ticks;
        public long GetTimestamp() => Ticks;
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    }
}
