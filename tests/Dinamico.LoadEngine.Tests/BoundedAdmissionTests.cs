using System.Collections.Concurrent;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Scheduling;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class BoundedAdmissionTests
{
    [Fact]
    public async Task Concurrent_admissions_never_exceed_capacity_and_every_start_finishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = new ConcurrentBag<RequestObservation>();
        var admission = new BoundedAdmission(4, async (index, _) =>
        {
            await release.Task;
            return Completed(index);
        }, observations.Add);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            Task.Run(() => admission.TryStart(new(index, default)))));
        Assert.Equal(4, admission.Snapshot.Started);
        Assert.Equal(4, admission.Snapshot.InFlight);
        Assert.Equal(96, admission.Snapshot.ConcurrencyLimit);
        admission.StopAdmissions();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.DrainAsync(cancelled.Token));
        release.SetResult();
        await admission.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, admission.Snapshot.Completed);
        Assert.Equal(4, observations.Select(observation => observation.ArrivalIndex).Distinct().Count());
        Assert.Equal(0, admission.Snapshot.InFlight);
    }

    [Fact]
    public async Task Scheduler_and_real_slow_HTTP_reconcile_without_waiting_for_responses()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429, RetryAfterValues = ["5"], HeaderDelay = TimeSpan.FromSeconds(1)
        });
        var validated = LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = fixture.Address.AbsoluteUri, RequestsPerSecond = 100,
            DurationSeconds = 1, MaxConcurrency = 2, RequestTimeout = TimeSpan.FromSeconds(10)
        }, Guid.NewGuid());
        Assert.True(validated.IsValid);
        using var transport = new LocalHttpTransport(validated.Plan!);
        var observed = new ConcurrentBag<RequestObservation>();
        var admission = new BoundedAdmission(validated.Plan!, transport, observed.Add);
        var clock = new TestClock();
        var scheduler = new ArrivalScheduler(ArrivalSchedule.FromPlan(validated.Plan!), clock, 0);
        // Advance only scheduler time; real HTTP stays in flight across all arrivals.
        for (int milliseconds = 1; milliseconds <= 1000; milliseconds++)
        {
            clock.Ticks = milliseconds * TimeSpan.TicksPerMillisecond;
            var wake = scheduler.Poll();
            for (long i = 0; i < wake.ArrivalCount; i++) admission.TryStart(wake[i]);
        }
        Assert.True(scheduler.IsCompleted);
        Assert.Equal(100, scheduler.ScheduledDue);
        Assert.Equal(0, scheduler.SchedulerLate);
        Assert.Equal(2, admission.Snapshot.Started);
        Assert.Equal(98, admission.Snapshot.ConcurrencyLimit);
        admission.StopAdmissions();
        await admission.DrainAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(admission.Snapshot.Started, fixture.ReceivedCount);
        Assert.Equal(admission.Snapshot.Started, admission.Snapshot.Completed);
        Assert.All(observed, observation =>
        {
            Assert.Equal(RequestOutcome.HttpCompleted, observation.Outcome);
            Assert.True(observation.IsRateLimited);
            Assert.Equal(TimeSpan.FromSeconds(5), observation.RetryAfter.Delay);
        });
    }

    [Fact]
    public async Task Saturation_drops_immediately_and_released_slot_is_reusable()
    {
        var pending = new ConcurrentDictionary<long, TaskCompletionSource<RequestObservation>>();
        var observations = new ConcurrentBag<RequestObservation>();
        var admission = new BoundedAdmission(2, (index, _) =>
        {
            var source = new TaskCompletionSource<RequestObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[index] = source;
            return source.Task;
        }, observations.Add);
        Assert.True(admission.TryStart(new(0, default)));
        Assert.True(admission.TryStart(new(1, default)));
        Assert.False(admission.TryStart(new(2, default)));
        Assert.Equal(2, admission.Snapshot.InFlight);
        pending[0].SetResult(Completed(0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (admission.Snapshot.Completed == 0) await Task.Delay(1, timeout.Token);
        Assert.True(admission.TryStart(new(3, default)));
        pending[1].SetResult(Completed(1));
        pending[3].SetResult(Completed(3));
        admission.StopAdmissions();
        await admission.DrainAsync(timeout.Token);
        Assert.Equal(new AdmissionSnapshot(3, 3, 0, 2, 1, 0), admission.Snapshot);
        Assert.Equal(3, observations.Count);
        Assert.True(admission.Snapshot.GeneratorLimited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_faults_produce_terminal_internal_error_and_release_slot(bool asynchronous)
    {
        var observations = new List<RequestObservation>();
        var admission = new BoundedAdmission(1, (_, _) => asynchronous
            ? Task.FromException<RequestObservation>(new IOException("synthetic"))
            : throw new IOException("synthetic"), observations.Add);
        Assert.True(admission.TryStart(new(0, default)));
        Assert.True(admission.TryStart(new(1, default)));
        admission.StopAdmissions();
        await admission.DrainAsync();
        Assert.All(observations, observation => Assert.Equal(RequestOutcome.InternalFailed, observation.Outcome));
        Assert.Equal(2, admission.Snapshot.Completed);
        Assert.Equal(0, admission.Snapshot.InFlight);
    }

    [Fact]
    public async Task Cancellation_stops_new_starts_and_drains_active_operation()
    {
        using var stop = new CancellationTokenSource();
        var admission = new BoundedAdmission(1, async (index, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { }
            return new(index, RequestOutcome.CancelledInFlight, default, null, 0, RequestErrorKind.Cancellation);
        }, _ => { });
        Assert.True(admission.TryStart(new(0, default), stop.Token));
        stop.Cancel();
        Assert.False(admission.TryStart(new(1, default), stop.Token));
        admission.StopAdmissions();
        await admission.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, admission.Snapshot.Completed);
        Assert.Equal(1, admission.Snapshot.Policy);
    }

    [Fact]
    public async Task Observer_failure_is_reported_by_drain_and_does_not_leak_slot()
    {
        var admission = new BoundedAdmission(1, (index, _) => Task.FromResult(Completed(index)),
            _ => throw new InvalidOperationException("synthetic observer"));
        Assert.True(admission.TryStart(new(0, default)));
        Assert.False(admission.TryStart(new(1, default)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.DrainAsync());
        Assert.Equal(0, admission.Snapshot.InFlight);
        Assert.Equal(1, admission.Snapshot.Completed);
    }

    [Fact]
    public async Task Large_total_does_not_retain_tasks_or_exceed_capacity()
    {
        long observed = 0;
        var admission = new BoundedAdmission(1, (index, _) => Task.FromResult(Completed(index)), _ => observed++);
        for (int i = 0; i < 100_000; i++) Assert.True(admission.TryStart(new(i, default)));
        admission.StopAdmissions();
        await admission.DrainAsync();
        Assert.Equal(100_000, observed);
        Assert.Equal(100_000, admission.Snapshot.Completed);
        Assert.Equal(1, admission.Snapshot.PeakInFlight);
    }

    private static RequestObservation Completed(long index) => new(index,
        RequestOutcome.HttpCompleted, default, 429, 0);

    private sealed class TestClock : IMonotonicClock
    {
        internal long Ticks { get; set; }
        public long GetTimestamp() => Ticks;
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    }
}
