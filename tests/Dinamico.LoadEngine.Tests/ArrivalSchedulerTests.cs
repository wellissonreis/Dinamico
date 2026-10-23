using System.Numerics;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Scheduling;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class ArrivalSchedulerTests
{
    [Fact]
    public void One_request_uses_half_interval_and_finishes_at_exact_duration()
    {
        var clock = new FakeClock();
        var schedule = new ArrivalSchedule(1, 1);
        var scheduler = NewScheduler(schedule, clock);
        Assert.Equal(TimeSpan.FromMilliseconds(500), schedule.GetArrival(0).Due);
        Assert.Equal(2, schedule.MaxArrivalsPerWake);
        clock.SetElapsed(TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(1));
        Assert.Equal(0, scheduler.Poll().DueCount);
        clock.Advance(TimeSpan.FromTicks(1));
        var due = scheduler.Poll();
        Assert.Equal(1, due.ArrivalCount);
        Assert.Equal(new ArrivalIntent(0, TimeSpan.FromMilliseconds(500)), due[0]);
        Assert.False(due.IsCompleted);
        clock.SetElapsed(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        Assert.False(scheduler.Poll().IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(scheduler.Poll().IsCompleted);
        long wakes = scheduler.WakeCount;
        Assert.True(scheduler.Poll().IsCompleted);
        Assert.Equal(wakes, scheduler.WakeCount);
        Assert.Equal(1, scheduler.ScheduledDue);
        Assert.Equal(0, scheduler.SchedulerLate);
        Assert.Equal(0, scheduler.NotScheduled);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 1)]
    [InlineData(1_000, 1)]
    [InlineData(50_000, 1)]
    public async Task Quantum_wakes_offer_exact_count_and_increasing_absolute_indices(long rate, long seconds)
    {
        var clock = new FakeClock();
        var wait = new AdvancingWait(clock);
        var schedule = new ArrivalSchedule(rate, seconds);
        var scheduler = NewScheduler(schedule, clock, wait);
        long expectedIndex = 0;
        while (!scheduler.IsCompleted)
        {
            var batch = await scheduler.WaitForNextWakeAsync();
            Assert.True(batch.ArrivalCount <= schedule.MaxArrivalsPerWake);
            Assert.Equal(batch.DueCount, batch.ArrivalCount + batch.SchedulerLate);
            for (long offset = 0; offset < batch.ArrivalCount; offset++)
            {
                var intent = batch[offset];
                Assert.Equal(expectedIndex, intent.Index);
                Assert.Equal(ReferenceDue(expectedIndex++, rate), intent.Due);
                Assert.True(intent.Due <= clock.Elapsed);
                Assert.True(intent.Due < schedule.Duration);
            }
        }
        Assert.Equal(rate * seconds, expectedIndex);
        Assert.Equal(expectedIndex, scheduler.ScheduledDue);
        Assert.Equal(expectedIndex, scheduler.OfferedArrivals);
        Assert.Equal(0, scheduler.SchedulerLate);
        Assert.Equal(0, scheduler.LimitedWakeCount);
        Assert.True(wait.CallCount <= seconds * 1_000 + 1);
    }

    [Fact]
    public void Rational_due_rounds_up_without_early_arrival_or_drift()
    {
        var schedule = new ArrivalSchedule(3, 1);
        Assert.Equal(1_666_667, schedule.GetArrival(0).Due.Ticks);
        Assert.Equal(0, schedule.CountDue(TimeSpan.FromTicks(1_666_666)));
        Assert.Equal(1, schedule.CountDue(TimeSpan.FromTicks(1_666_667)));
        Assert.Equal(5_000_000, schedule.GetArrival(1).Due.Ticks);
        Assert.Equal(8_333_334, schedule.GetArrival(2).Due.Ticks);
    }

    [Fact]
    public async Task Final_microbatch_is_offered_before_the_end_quantum_boundary()
    {
        var clock = new FakeClock();
        var wait = new AdvancingWait(clock);
        var scheduler = NewScheduler(new ArrivalSchedule(1_000, 1), clock, wait);
        clock.SetElapsed(TimeSpan.FromMilliseconds(999));
        scheduler.Poll();
        var final = await scheduler.WaitForNextWakeAsync();
        Assert.Equal(TimeSpan.FromTicks(5_000), wait.LastDelay);
        Assert.Equal(TimeSpan.FromTicks(9_995_000), clock.Elapsed);
        Assert.Equal(1, final.ArrivalCount);
        Assert.Equal(999, final[0].Index);
        Assert.False(final.IsCompleted);
        Assert.True((await scheduler.WaitForNextWakeAsync()).IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Elapsed);
    }

    [Fact]
    public void Wake_snapshot_remains_immutable_after_scheduler_advances_and_indexing_is_bounded()
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(1_000, 1), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(1));
        var first = scheduler.Poll();
        clock.SetElapsed(TimeSpan.FromMilliseconds(2));
        var second = scheduler.Poll();
        Assert.Equal(new ArrivalIntent(0, TimeSpan.FromTicks(5_000)), first[0]);
        Assert.Equal(1, second[0].Index);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = first[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = first[first.ArrivalCount]);
    }

    [Fact]
    public void Long_simulated_run_matches_independent_rational_oracle_at_distant_indices()
    {
        const long rate = 49_999;
        const long seconds = 100_000_000;
        var schedule = new ArrivalSchedule(rate, seconds);
        long[] indices = [0, 1, 99_999, 1_000_000_001, schedule.PlannedTotal / 2, schedule.PlannedTotal - 1];
        foreach (long index in indices)
        {
            var due = schedule.GetArrival(index).Due;
            Assert.Equal(ReferenceDue(index, rate), due);
            Assert.Equal(index, schedule.CountDue(due - TimeSpan.FromTicks(1)));
            Assert.Equal(index + 1, schedule.CountDue(due));
        }
        var clock = new FakeClock();
        var scheduler = NewScheduler(schedule, clock);
        long distant = schedule.PlannedTotal - 10;
        clock.SetElapsed(schedule.GetArrival(distant).Due);
        var batch = scheduler.Poll();
        Assert.Equal(distant + 1, batch.DueCount);
        Assert.Equal(schedule.MaxArrivalsPerWake, batch.ArrivalCount);
        Assert.Equal(batch.DueCount - batch.ArrivalCount, batch.SchedulerLate);
        Assert.Equal(1, scheduler.WakeCount);
    }

    [Fact]
    public void Large_pause_skips_trillions_in_one_wake_and_never_catches_up()
    {
        var schedule = new ArrivalSchedule(1_000_000_000, 1_000_000);
        var clock = new FakeClock();
        var scheduler = NewScheduler(schedule, clock);
        clock.SetElapsed(TimeSpan.FromSeconds(900_000));
        var batch = scheduler.Poll();
        Assert.Equal(900_000_000_000_000, batch.DueCount);
        Assert.Equal(1_000_001, batch.ArrivalCount);
        Assert.Equal(batch.DueCount - batch.ArrivalCount, batch.SchedulerLate);
        Assert.Equal(1, scheduler.LimitedWakeCount);
        Assert.Equal(TimeSpan.FromTicks(clock.Elapsed.Ticks - 1), scheduler.MaxObservedLateness);
        var repeated = scheduler.Poll();
        Assert.Equal(0, repeated.DueCount);
        Assert.Equal(0, repeated.ArrivalCount);
        Assert.Equal(batch.DueCount, scheduler.ScheduledDue);
        Assert.Equal(scheduler.ScheduledDue, scheduler.OfferedArrivals + scheduler.SchedulerLate);
    }

    [Fact]
    public void Wake_cap_drops_acceptable_excess_instead_of_queueing_it()
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(1_000, 1), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(5));
        var batch = scheduler.Poll();
        Assert.Equal(5, batch.DueCount);
        Assert.Equal(2, batch.ArrivalCount);
        Assert.Equal(3, batch.SchedulerLate);
        Assert.Equal(0, batch[0].Index);
        Assert.Equal(1, batch[1].Index);
        Assert.Equal(1, scheduler.LimitedWakeCount);
        Assert.Equal(TimeSpan.FromTicks(45_000), scheduler.MaxObservedLateness);
        Assert.Equal(0, scheduler.Poll().ArrivalCount);
        clock.SetElapsed(TimeSpan.FromMilliseconds(6));
        var next = scheduler.Poll();
        Assert.Equal(1, next.ArrivalCount);
        Assert.Equal(5, next[0].Index);
        Assert.Equal(3, scheduler.SchedulerLate);
    }

    [Fact]
    public void Tolerance_is_inclusive_but_one_tick_beyond_is_late()
    {
        var firstDue = TimeSpan.FromMilliseconds(500);
        var tolerance = TimeSpan.FromMilliseconds(5);
        foreach (bool beyond in new[] { false, true })
        {
            var clock = new FakeClock();
            var scheduler = NewScheduler(new ArrivalSchedule(1, 1), clock);
            clock.SetElapsed(firstDue + tolerance + TimeSpan.FromTicks(beyond ? 1 : 0));
            var batch = scheduler.Poll();
            Assert.Equal(beyond ? 0 : 1, batch.ArrivalCount);
            Assert.Equal(beyond ? 1 : 0, batch.SchedulerLate);
            Assert.Equal(clock.Elapsed - firstDue, scheduler.MaxObservedLateness);
        }
    }

    [Fact]
    public void Zero_tolerance_accepts_exact_due_only()
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(2, 1, latenessTolerance: TimeSpan.Zero), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, scheduler.Poll()[0].Index);
        clock.SetElapsed(TimeSpan.FromMilliseconds(750) + TimeSpan.FromTicks(1));
        Assert.Equal(1, scheduler.Poll().SchedulerLate);
        Assert.Equal(1, scheduler.OfferedArrivals);
    }

    [Fact]
    public void Drop_diagnostics_partition_lateness_capacity_and_end_without_double_counting()
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(1_000, 1), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(10));
        var first = scheduler.Poll(); // 5 outside tolerance, 5 acceptable, only 2 offered.
        Assert.Equal(5, scheduler.ToleranceExceededDrops);
        Assert.Equal(3, scheduler.WakeCapacityDrops);
        Assert.Equal(8, first.SchedulerLate);
        clock.SetElapsed(TimeSpan.FromSeconds(1));
        scheduler.Poll();
        Assert.Equal(990, scheduler.EndBoundaryDrops);
        Assert.Equal(scheduler.SchedulerLate,
            scheduler.ToleranceExceededDrops + scheduler.WakeCapacityDrops + scheduler.EndBoundaryDrops);
        Assert.Equal(1_000, scheduler.ScheduledDue);
        Assert.Equal(2, scheduler.OfferedArrivals);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void Reaching_or_passing_end_never_offers_remaining_due(int milliseconds)
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(1_000, 1,
            latenessTolerance: TimeSpan.FromDays(1)), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(milliseconds));
        var batch = scheduler.Poll();
        Assert.True(batch.IsCompleted);
        Assert.Equal(1_000, batch.DueCount);
        Assert.Equal(1_000, batch.SchedulerLate);
        Assert.Equal(0, batch.ArrivalCount);
        Assert.Equal(0, scheduler.NotScheduled);
    }

    [Fact]
    public void Extremely_high_rate_keeps_final_quantized_due_at_end_as_a_drop()
    {
        var schedule = new ArrivalSchedule(long.MaxValue, 1);
        Assert.Equal(long.MaxValue, schedule.PlannedTotal);
        Assert.Equal(schedule.Duration, schedule.GetArrival(schedule.PlannedTotal - 1).Due);
        var clock = new FakeClock();
        var scheduler = NewScheduler(schedule, clock);
        clock.SetElapsed(schedule.Duration);
        Assert.Equal(long.MaxValue, scheduler.Poll().SchedulerLate);
        Assert.Equal(0, scheduler.OfferedArrivals);
    }

    [Fact]
    public async Task Zero_rate_has_no_arrivals_and_waits_to_end_with_constant_state()
    {
        var clock = new FakeClock();
        var wait = new AdvancingWait(clock);
        var schedule = new ArrivalSchedule(0, 1);
        var scheduler = NewScheduler(schedule, clock, wait);
        Assert.Equal(0, schedule.PlannedTotal);
        Assert.Equal(0, schedule.CountDue(TimeSpan.MaxValue));
        var batch = await scheduler.WaitForNextWakeAsync();
        Assert.True(batch.IsCompleted);
        Assert.Equal(0, batch.DueCount);
        Assert.Equal(1, wait.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(1), wait.LastDelay);
    }

    [Theory]
    [InlineData(10, 1, 3)]
    [InlineData(2, 1, 7)]
    [InlineData(1, 3, 8)]
    [InlineData(7, 3, 4)]
    [InlineData(0, 1, 3)]
    public void Partition_union_has_every_global_index_once_including_rate_below_workers(
        long rate, long seconds, int workers)
    {
        var global = new ArrivalSchedule(rate, seconds);
        var all = new List<ArrivalIntent>();
        for (int slot = 0; slot < workers; slot++)
        {
            var partition = new ArrivalSchedule(rate, seconds, workerCount: workers, workerSlot: slot);
            long previous = -1;
            for (long ordinal = 0; ordinal < partition.AssignedTotal; ordinal++)
            {
                var intent = partition.GetArrival(ordinal);
                Assert.Equal(slot, intent.Index % workers);
                Assert.True(intent.Index > previous);
                previous = intent.Index;
                Assert.Equal(ReferenceDue(intent.Index, rate), intent.Due);
                all.Add(intent);
            }
            Assert.Equal(partition.AssignedTotal, partition.CountDue(partition.Duration));
        }
        all.Sort((left, right) => left.Index.CompareTo(right.Index));
        Assert.Equal(global.PlannedTotal, all.Count);
        for (int index = 0; index < all.Count; index++)
            Assert.Equal(global.GetArrival(index), all[index]);
    }

    [Fact]
    public void Partition_due_counts_match_enumeration_at_every_tick_boundary()
    {
        const int workers = 3;
        var global = new ArrivalSchedule(10, 1);
        for (int slot = 0; slot < workers; slot++)
        {
            var schedule = new ArrivalSchedule(10, 1, workerCount: workers, workerSlot: slot);
            var arrivals = Enumerable.Range(0, (int)schedule.AssignedTotal)
                .Select(ordinal => schedule.GetArrival(ordinal)).ToArray();
            for (long index = 0; index < global.PlannedTotal; index++)
            {
                var due = global.GetArrival(index).Due;
                foreach (var instant in new[] { due - TimeSpan.FromTicks(1), due, due + TimeSpan.FromTicks(1) })
                    Assert.Equal(arrivals.LongCount(arrival => arrival.Due <= instant), schedule.CountDue(instant));
            }
        }
    }

    [Fact]
    public void Partition_wake_cap_uses_conservative_local_rate_and_honors_custom_quantum()
    {
        Assert.Equal(18, new ArrivalSchedule(50_000, 1, workerCount: 3).MaxArrivalsPerWake);
        Assert.Equal(101, new ArrivalSchedule(50_000, 1,
            quantum: TimeSpan.FromMilliseconds(2)).MaxArrivalsPerWake);
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(10, 1, workerCount: 3, workerSlot: 2,
            latenessTolerance: TimeSpan.FromSeconds(1)), clock);
        clock.SetElapsed(TimeSpan.FromMilliseconds(950));
        var batch = scheduler.Poll();
        Assert.Equal(3, batch.DueCount);
        Assert.Equal(2, batch.ArrivalCount);
        Assert.Equal(2, batch[0].Index);
        Assert.Equal(5, batch[1].Index);
        Assert.Equal(1, batch.SchedulerLate);
    }

    [Fact]
    public void Partition_at_long_maximum_does_not_overflow_index_stride_or_rate_ceiling()
    {
        var schedule = new ArrivalSchedule(long.MaxValue, 1,
            workerCount: int.MaxValue, workerSlot: int.MaxValue - 1);
        var last = schedule.GetArrival(schedule.AssignedTotal - 1);
        Assert.True(last.Index < long.MaxValue);
        Assert.Equal(int.MaxValue - 1, last.Index % int.MaxValue);
        Assert.Equal(ReferenceDue(last.Index, long.MaxValue), last.Due);
        Assert.Equal(schedule.AssignedTotal, schedule.CountDue(schedule.Duration));
        Assert.Equal(4_294_969, schedule.MaxArrivalsPerWake);
    }

    [Fact]
    public void Invalid_parameters_and_overflow_are_rejected_without_wrapping()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, latenessTolerance: TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, workerCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, workerSlot: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArrivalSchedule(1, 1, workerCount: 2, workerSlot: 2));
        Assert.Throws<OverflowException>(() => new ArrivalSchedule(long.MaxValue, 2));
        Assert.Throws<OverflowException>(() => new ArrivalSchedule(0, long.MaxValue));
        Assert.Throws<OverflowException>(() => new ArrivalSchedule(long.MaxValue, 1, TimeSpan.MaxValue));
        Assert.Throws<ArgumentNullException>(() => ArrivalSchedule.FromPlan(null!));
        var schedule = new ArrivalSchedule(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.GetArrival(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.GetArrival(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = default(SchedulerWake)[0]);
    }

    [Fact]
    public async Task Opaque_clock_units_and_nonzero_origin_do_not_change_due_or_absolute_waits()
    {
        var clock = new FakeClock(origin: 9_000_123, unitsPerTick: 37);
        var wait = new AdvancingWait(clock, TimeSpan.FromMilliseconds(2));
        var scheduler = NewScheduler(new ArrivalSchedule(3, 1), clock, wait);
        var first = await scheduler.WaitForNextWakeAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(167), wait.LastDelay);
        Assert.Equal(ReferenceDue(0, 3), first[0].Due);
        var second = await scheduler.WaitForNextWakeAsync();
        Assert.Equal(TimeSpan.FromMilliseconds(331), wait.LastDelay);
        Assert.Equal(ReferenceDue(1, 3), second[0].Due);
        Assert.Equal(9_000_123, scheduler.StartingTimestamp);
    }

    [Fact]
    public async Task Early_wakeup_rewaits_positive_remaining_time_and_large_oversleep_drops_due()
    {
        var clock = new FakeClock();
        var early = new EarlyWait(clock);
        var scheduler = NewScheduler(new ArrivalSchedule(1, 1), clock, early);
        Assert.Equal(0, (await scheduler.WaitForNextWakeAsync()).DueCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), early.LastDelay);
        var batch = await scheduler.WaitForNextWakeAsync();
        Assert.Equal(TimeSpan.FromTicks(1), early.LastDelay);
        Assert.Equal(1, batch.ArrivalCount);

        var pausedClock = new FakeClock();
        var pausedWait = new AdvancingWait(pausedClock, TimeSpan.FromSeconds(10));
        var paused = NewScheduler(new ArrivalSchedule(1_000, 1), pausedClock, pausedWait);
        var stopped = await paused.WaitForNextWakeAsync();
        Assert.True(stopped.IsCompleted);
        Assert.Equal(1_000, stopped.SchedulerLate);
        Assert.Equal(0, stopped.ArrivalCount);
    }

    [Fact]
    public async Task Cancellation_before_or_during_wait_does_not_consume_due()
    {
        using var source = new CancellationTokenSource();
        var clock = new FakeClock();
        var wait = new CancellingWait(source);
        var scheduler = NewScheduler(new ArrivalSchedule(1, 1), clock, wait);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scheduler.WaitForNextWakeAsync(source.Token));
        Assert.Equal(0, scheduler.ScheduledDue);
        Assert.Equal(0, scheduler.WakeCount);
        Assert.Equal(1, scheduler.NotScheduled);
        Assert.False(scheduler.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await scheduler.WaitForNextWakeAsync(source.Token));
        Assert.Equal(1, wait.CallCount);
    }

    [Fact]
    public void Monotonic_regression_is_refused_and_future_origin_has_no_early_arrivals()
    {
        var clock = new FakeClock();
        var scheduler = NewScheduler(new ArrivalSchedule(1, 1), clock);
        clock.SetElapsed(TimeSpan.FromTicks(-10));
        Assert.Equal(0, scheduler.Poll().DueCount);
        clock.SetElapsed(TimeSpan.Zero);
        Assert.Equal(0, scheduler.Poll().DueCount);
        clock.SetElapsed(TimeSpan.FromTicks(-1));
        Assert.Throws<InvalidOperationException>(() => scheduler.Poll());
        Assert.Equal(0, scheduler.ScheduledDue);
    }

    [Fact]
    public async Task Unrepresentable_wait_from_future_origin_is_rejected_without_consuming_arrivals()
    {
        var clock = new FakeClock();
        var wait = new AdvancingWait(clock);
        var scheduler = NewScheduler(new ArrivalSchedule(1, 1), clock, wait);
        clock.SetElapsed(TimeSpan.MinValue);
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await scheduler.WaitForNextWakeAsync());
        Assert.Equal(0, wait.CallCount);
        Assert.Equal(0, scheduler.ScheduledDue);
        Assert.Equal(0, scheduler.WakeCount);
    }

    [Theory]
    [InlineData(0)] // 429 contínuo; delta válido.
    [InlineData(1)] // 429 intermitente; Retry-After limitado ao teto.
    [InlineData(2)] // 503 com Retry-After não aplicável.
    [InlineData(3)] // 429 com data civil, duração fictícia lenta.
    public async Task Modes_and_fictitious_responses_preserve_exact_open_loop_sequence(int scenario)
    {
        var baseline = await CaptureSequence(RateLimitMode.Normal, -1);
        foreach (var mode in Enum.GetValues<RateLimitMode>())
        {
            var observed = await CaptureSequence(mode, scenario);
            Assert.Equal(baseline.Arrivals, observed.Arrivals);
            Assert.Equal(baseline.Wakes, observed.Wakes);
            Assert.Equal(baseline.End, observed.End);
            Assert.Equal(2_000, observed.Arrivals.Count);
            Assert.Equal(2_000, observed.ScheduledDue);
            Assert.Equal(0, observed.Drops);
            Assert.Equal(2_000, observed.PlannedTotal);
        }
    }

    private static async Task<Sequence> CaptureSequence(RateLimitMode mode, int scenario)
    {
        var configuration = new LoadTestConfiguration
        {
            Url = "http://127.0.0.1/", RequestsPerSecond = 1_000, DurationSeconds = 2,
            RateLimitMode = mode, Headers = [new("X-Test-Key", "synthetic")],
            TestCredentialHeaderIndex = 0, TestAuthorizationDeclared = true
        };
        var plan = LocalPlanValidator.Validate(configuration, Guid.Parse("97864031-5f4d-4934-b4e5-cbc53e5b8009")).Plan!;
        var schedule = ArrivalSchedule.FromPlan(plan);
        var clock = new FakeClock();
        var scheduler = NewScheduler(schedule, clock, new AdvancingWait(clock));
        var arrivals = new List<ArrivalIntent>();
        while (!scheduler.IsCompleted)
        {
            var batch = await scheduler.WaitForNextWakeAsync();
            for (long offset = 0; offset < batch.ArrivalCount; offset++)
            {
                var intent = batch[offset];
                arrivals.Add(intent);
                if (scenario >= 0)
                {
                    int status = scenario == 2 ? 503 : scenario == 1 && intent.Index % 2 == 0 ? 200 : 429;
                    string value = scenario switch
                    {
                        1 => "999999999999999999999999999999",
                        3 => "Fri, 01 Jan 2100 00:00:00 GMT",
                        _ => "120"
                    };
                    var evidence = RetryAfterObservation.Parse(status, [value], DateTimeOffset.UnixEpoch);
                    var response = new RequestObservation(intent.Index, RequestOutcome.HttpCompleted,
                        TimeSpan.FromSeconds(60), status, 0, retryAfter: evidence);
                    Assert.Equal(status == 429, response.IsRateLimited);
                    Assert.Equal(TimeSpan.FromSeconds(60), response.TerminalDuration);
                    if (status == 429)
                    {
                        Assert.Equal(scenario == 3 ? RetryAfterKind.HttpDate : RetryAfterKind.DeltaSeconds,
                            response.RetryAfter.Kind);
                        Assert.Equal(scenario is 1 or 3, response.RetryAfter.WasClamped);
                    }
                    else
                        Assert.Equal(RetryAfterKind.NotApplicable, response.RetryAfter.Kind);
                    // Consumir observação não avança tempo nem aguarda término de operação.
                }
            }
        }
        return new(arrivals, scheduler.WakeCount, clock.Elapsed,
            scheduler.ScheduledDue, scheduler.SchedulerLate, schedule.PlannedTotal);
    }

    private static TimeSpan ReferenceDue(long index, long rate)
    {
        BigInteger numerator = (2 * (BigInteger)index + 1) * TimeSpan.TicksPerSecond;
        BigInteger denominator = 2 * (BigInteger)rate;
        return TimeSpan.FromTicks((long)((numerator + denominator - 1) / denominator));
    }

    private static ArrivalScheduler NewScheduler(ArrivalSchedule schedule, FakeClock clock,
        ISchedulerWait? wait = null) => new(schedule, clock, clock.Origin, wait);

    private sealed record Sequence(List<ArrivalIntent> Arrivals, long Wakes, TimeSpan End,
        long ScheduledDue, long Drops, long PlannedTotal);

    private sealed class FakeClock(long origin = 0, long unitsPerTick = 1) : IMonotonicClock
    {
        private long _timestamp = origin;
        public long Origin { get; } = origin;
        public TimeSpan Elapsed => GetElapsedTime(Origin, _timestamp);
        public long GetTimestamp() => _timestamp;
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(checked(endingTimestamp - startingTimestamp) / unitsPerTick);
        public void SetElapsed(TimeSpan elapsed) =>
            _timestamp = checked(Origin + checked(elapsed.Ticks * unitsPerTick));
        public void Advance(TimeSpan interval) => SetElapsed(Elapsed + interval);
    }

    private sealed class AdvancingWait(FakeClock clock, TimeSpan oversleep = default) : ISchedulerWait
    {
        public long CallCount { get; private set; }
        public TimeSpan LastDelay { get; private set; }
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(delay > TimeSpan.Zero);
            CallCount++;
            LastDelay = delay;
            clock.Advance(delay + oversleep);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EarlyWait(FakeClock clock) : ISchedulerWait
    {
        private bool _first = true;
        public TimeSpan LastDelay { get; private set; }
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(delay > TimeSpan.Zero);
            LastDelay = delay;
            clock.Advance(delay - TimeSpan.FromTicks(_first ? 1 : 0));
            _first = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingWait(CancellationTokenSource source) : ISchedulerWait
    {
        public int CallCount { get; private set; }
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            CallCount++;
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
