using Dinamico.LoadEngine.Scheduling;
using Dinamico.LoadEngine.Timing;

namespace Dinamico.LoadEngine.Tests;

public sealed class SchedulerWaitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1_000)]
    public async Task Real_scheduler_never_offers_an_arrival_before_its_absolute_due(int rate)
    {
        var clock = StopwatchMonotonicClock.Instance;
        long origin = clock.GetTimestamp();
        var scheduler = new ArrivalScheduler(new ArrivalSchedule(rate, 1), clock, origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long offered = 0;
        while (!scheduler.IsCompleted)
        {
            var batch = await scheduler.WaitForNextWakeAsync(timeout.Token);
            var elapsed = clock.GetElapsedTime(origin, clock.GetTimestamp());
            for (long offset = 0; offset < batch.ArrivalCount; offset++)
            {
                Assert.True(batch[offset].Due <= elapsed);
                Assert.True(batch[offset].Due < TimeSpan.FromSeconds(1));
                offered++;
            }
        }
        // OS jitter may cause drops; it must never cause early offers or lose accounting.
        Assert.Equal(rate, scheduler.ScheduledDue);
        Assert.Equal(rate, offered + scheduler.SchedulerLate);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_728_000_000_000)]
    public async Task Cancelled_waits_release_registration_and_subsequent_waits_work(long ticks)
    {
        var wait = new SchedulerWait();
        for (int iteration = 0; iteration < 40; iteration++)
        {
            using var stop = new CancellationTokenSource();
            var pending = wait.DelayAsync(TimeSpan.FromTicks(ticks), stop.Token).AsTask();
            stop.Cancel();
            if (ticks > TimeSpan.TicksPerSecond)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                // A short timer may win before the caller gets scheduled to cancel.
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { Assert.True(stop.IsCancellationRequested); }
            }
        }
        await wait.DelayAsync(TimeSpan.FromMilliseconds(1), default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5_000)]
    [InlineData(100_000)]
    [InlineData(100_001)]
    public async Task Fine_and_managed_wait_boundaries_complete_and_respect_pre_cancellation(long ticks)
    {
        var wait = new SchedulerWait();
        await wait.DelayAsync(TimeSpan.FromTicks(ticks), default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.DelayAsync(TimeSpan.FromTicks(ticks), stop.Token).AsTask());
    }

    [Fact]
    public async Task Invalid_delays_and_pre_cancelled_token_do_not_start_a_wait()
    {
        var wait = new SchedulerWait();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wait.DelayAsync(TimeSpan.Zero, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wait.DelayAsync(TimeSpan.FromTicks(-1), default).AsTask());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.DelayAsync(TimeSpan.FromDays(2), stop.Token).AsTask());
    }
}
