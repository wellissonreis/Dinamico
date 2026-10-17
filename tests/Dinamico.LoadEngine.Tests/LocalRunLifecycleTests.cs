using System.Collections.Concurrent;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Scheduling;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class LocalRunLifecycleTests
{
    [Fact]
    public async Task NaturalDrainDoesNotCancelActiveIoAndKeepsHeadersInTheirOwnWindow()
    {
        var time = new VirtualTime();
        var transport = new GatedTransport(time, 429);
        var output = new Output();
        var runner = Runner(time, transport);
        Task<LocalRunResult> pending = runner.RunAsync(Plan(rate: 1, timeoutSeconds: 20), output);
        await WaitUntil(() => runner.State == LocalRunState.Draining);
        Assert.False(transport.Cancelled);
        time.Advance(TimeSpan.FromSeconds(4)); // generation ended at 1 s, monitor rotates at 5 s.
        await WaitUntil(() => output.Written.Count == 1);
        Assert.Equal(1UL, output.Written.Single().InFlight);
        time.Advance(TimeSpan.FromSeconds(1));
        transport.Release.TrySetResult();
        LocalRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunCompletion.Completed, result.Completion);
        Assert.Equal(LocalRunReason.DurationElapsed, result.Reason);
        Assert.True(result.DataComplete);
        Assert.Equal(TimeSpan.FromSeconds(1), result.GenerationDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), result.DrainDuration);
        Assert.Equal(1UL, result.Totals.StatusReceived[329]);
        Assert.Equal(1UL, result.Totals.HttpCompletedDuration.Count);
        var windows = output.Written.ToArray();
        Assert.Equal(2, windows.Length);
        Assert.Equal(1UL, windows[0].StatusReceived[329]);
        Assert.Equal(0UL, windows[1].FinalStatuses);
        Assert.Equal(1UL, windows[1][MetricCounter.HttpCompleted]);
        Assert.True(result.Quality.HasFlag(MeasurementQuality.TargetErrors));
        Assert.False(transport.DisposedWhileActive);
        Assert.True(transport.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(new[] { LocalRunState.Prepared, LocalRunState.Generating, LocalRunState.Draining, LocalRunState.Finished },
            result.Transitions.Select(t => t.State));
    }

    [Fact]
    public async Task CancelBeforeStartStillClosesMetricsWithoutConstructingTransport()
    {
        var time = new VirtualTime();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        int constructed = 0;
        var runner = new LocalRunController(time, time, new AdvancingWait(time), _ =>
        {
            Interlocked.Increment(ref constructed);
            throw new InvalidOperationException();
        });
        var output = new Output();
        LocalRunResult result = await runner.RunAsync(Plan(), output, stop.Token);
        Assert.Equal(0, constructed);
        Assert.Equal(LocalRunCompletion.Cancelled, result.Completion);
        Assert.Equal(LocalRunReason.UserCancelled, result.Reason);
        Assert.Equal(0, result.ScheduledDue);
        Assert.Equal(result.PlannedTotal, result.NotScheduledAfterStop);
        Assert.True(result.DataComplete);
        Assert.Single(output.Written);
        Assert.DoesNotContain(result.Transitions, t => t.State == LocalRunState.Generating);
        Assert.Throws<InvalidOperationException>(() => { _ = runner.RunAsync(Plan(), output); }); // Never duplicate a run.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringGenerationOrDrainStopsAdmissionsAndBalancesTerminals(bool duringDrain)
    {
        var time = new VirtualTime();
        var transport = new GatedTransport(time, 200);
        using var stop = new CancellationTokenSource();
        var wait = new AdvancingWait(time, call => { if (!duringDrain && call == 2) stop.Cancel(); });
        var runner = Runner(time, transport, wait);
        Task<LocalRunResult> pending = runner.RunAsync(Plan(capacity: 1), new Output(), stop.Token);
        if (duringDrain)
        {
            await WaitUntil(() => runner.State == LocalRunState.Draining);
            stop.Cancel();
        }
        stop.Cancel(); // Repeat is idempotent.
        LocalRunResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunCompletion.Cancelled, result.Completion);
        Assert.Equal(LocalRunReason.UserCancelled, result.Reason);
        Assert.True(result.DataComplete);
        Assert.True(transport.Cancelled);
        Assert.Equal(1, transport.Starts);
        Assert.Equal(1UL, result.Totals[MetricCounter.CancelledInFlight]);
        Assert.Equal(0, result.Admission.InFlight);
        Assert.Equal(result.Admission.Started, result.Admission.Completed);
        Assert.Equal(result.PlannedTotal, result.ScheduledDue + result.NotScheduledAfterStop);
        Assert.Equal((ulong)result.ScheduledDue, result.Totals[MetricCounter.ScheduledDue]);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.Starts);
        Assert.False(transport.DisposedWhileActive);
    }

    [Fact]
    public async Task CancellationBetweenPollsAccountsOnlyArrivalsDueBeforeLatchedStop()
    {
        var time = new VirtualTime();
        using var stop = new CancellationTokenSource();
        var wait = new AdvancingWait(time, call =>
        {
            if (call != 1) return;
            time.Advance(TimeSpan.FromMilliseconds(300)); // first due=.05 + .30 = .35, four due.
            stop.Cancel();
        });
        var transport = new ImmediateTransport(time);
        LocalRunResult result = await Runner(time, transport, wait).RunAsync(Plan(), new Output(), stop.Token);
        Assert.Equal(4, result.ScheduledDue);
        Assert.Equal(6, result.NotScheduledAfterStop);
        Assert.Equal(4UL, result.Totals[MetricCounter.Policy]);
        Assert.Equal(0, transport.Starts);
        Assert.True(result.DataComplete);
    }

    [Fact]
    public async Task ConcurrencyDropsDoNotQueueRequestsOrInflateTheLastGauge()
    {
        var time = new VirtualTime();
        var transport = new GatedTransport(time, 202);
        var runner = Runner(time, transport);
        var pending = runner.RunAsync(Plan(capacity: 1), new Output());
        await WaitUntil(() => runner.State == LocalRunState.Draining);
        Assert.Equal(1, transport.Starts);
        transport.Release.TrySetResult();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9UL, result.Totals[MetricCounter.ConcurrencyLimit]);
        Assert.Equal(1UL, result.Totals[MetricCounter.HttpSuccess]);
        Assert.Equal(0UL, result.Totals.LastInFlight);
        Assert.True(result.Quality.HasFlag(MeasurementQuality.GeneratorLimited));
        Assert.Equal(LocalRunCompletion.Completed, result.Completion);
        Assert.True(result.DataComplete);
    }

    [Fact]
    public async Task SchedulerLatenessCanFinishProtocolCompletelyWhileGeneratorIsLimited()
    {
        var time = new VirtualTime();
        var transport = new ImmediateTransport(time);
        var wait = new AdvancingWait(time, _ => time.Advance(TimeSpan.FromMilliseconds(100)));
        var result = await Runner(time, transport, wait).RunAsync(Plan(rate: 1, seconds: 3), new Output());
        Assert.Equal(0, transport.Starts);
        Assert.Equal(3UL, result.Totals[MetricCounter.SchedulerLate]);
        Assert.Equal(0, result.NotScheduledAfterStop);
        Assert.Equal(LocalRunCompletion.Completed, result.Completion);
        Assert.True(result.Quality.HasFlag(MeasurementQuality.GeneratorLimited));
        Assert.True(result.DataComplete);
    }

    [Fact]
    public async Task UnexpectedTransportFaultStopsTheRunAndIsAnInternalTerminalNotSuccess()
    {
        var time = new VirtualTime();
        const string canary = "synthetic-sensitive-exception";
        var transport = new ImmediateTransport(time, fault: new IOException(canary));
        var result = await Runner(time, transport).RunAsync(Plan(), new Output());
        Assert.Equal(LocalRunCompletion.Failed, result.Completion);
        Assert.Equal(LocalRunReason.InternalFailure, result.Reason);
        Assert.Equal(1, transport.Starts);
        Assert.Equal(1UL, result.Totals[MetricCounter.InternalFailed]);
        Assert.Equal(0UL, result.Totals.HttpCompletedDuration.Count);
        Assert.Equal(9, result.NotScheduledAfterStop);
        Assert.True(result.DataComplete); // Failed execution, fully recorded evidence.
        Assert.DoesNotContain(canary, result.ToString());
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task CancellationKeepsFirstReasonWhenOutputFailsLater()
    {
        var time = new VirtualTime();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var output = new Output { OnFlush = _ => ValueTask.FromException(new IOException("synthetic")) };
        var result = await Runner(time, new ImmediateTransport(time)).RunAsync(Plan(), output, stop.Token);
        Assert.Equal(LocalRunReason.UserCancelled, result.Reason);
        Assert.Equal(LocalRunCompletion.Failed, result.Completion);
        Assert.True(result.Failures.HasFlag(LocalRunFailure.Output));
        Assert.False(result.FlushConfirmed);
        Assert.True(result.Quality.HasFlag(MeasurementQuality.MetricsPartial));
    }

    [Fact]
    public async Task BlockedWriterRetainsSixUnacknowledgedWindowsAndStopsWithAPartialPrefix()
    {
        var time = new VirtualTime();
        var transport = new ImmediateTransport(time);
        var entered = Signal();
        var output = new Output
        {
            OnWrite = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        };
        var runner = Runner(time, transport);
        Task<LocalRunResult> pending = runner.RunAsync(Plan(rate: 1, seconds: 60), output);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => time.HasTimerAfter(LocalRunController.FlushTimeout));
        Assert.Equal(LocalRunState.Stopping, runner.State);
        long startsAtStop = transport.Starts;
        time.Advance(LocalRunController.FlushTimeout);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunReason.TelemetryBackpressure, result.Reason);
        Assert.True(result.Failures.HasFlag(LocalRunFailure.TelemetryBackpressure));
        Assert.True(result.Failures.HasFlag(LocalRunFailure.FlushTimeout));
        Assert.Equal(6UL, result.Totals.WindowCount);
        Assert.False(result.MetricsCoverageComplete);
        Assert.False(result.DataComplete);
        Assert.True(result.OperationsDrained);
        Assert.True(result.OutputStopped);
        Assert.Equal(startsAtStop, transport.Starts);
        Assert.Empty(output.Written);
        Assert.Equal(30UL, result.Totals[MetricCounter.Started]);
    }

    [Fact]
    public async Task FailedWriterStopsTrafficAndPreservesQueuedEvidence()
    {
        var time = new VirtualTime();
        var transport = new ImmediateTransport(time);
        var output = new Output { OnWrite = (_, _) => ValueTask.FromException(new IOException("synthetic")) };
        var result = await Runner(time, transport).RunAsync(Plan(rate: 1, seconds: 6), output);
        Assert.Equal(LocalRunReason.OutputFailure, result.Reason);
        Assert.Equal(LocalRunCompletion.Failed, result.Completion);
        Assert.False(result.FlushConfirmed);
        Assert.True(result.Quality.HasFlag(MeasurementQuality.MetricsPartial));
        Assert.True(result.OperationsDrained);
        Assert.True(result.OutputStopped);
        Assert.True(result.Totals.WindowCount > 0);
    }

    [Fact]
    public async Task FlushDeadlineCancelsOutputWithoutPretendingDataWasConfirmed()
    {
        var time = new VirtualTime();
        var entered = Signal();
        var output = new Output { OnFlush = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        Task<LocalRunResult> pending = Runner(time, new ImmediateTransport(time)).RunAsync(Plan(), output);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => time.HasTimerAfter(LocalRunController.FlushTimeout));
        time.Advance(LocalRunController.FlushTimeout);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunReason.FlushTimedOut, result.Reason);
        Assert.Equal(LocalRunCompletion.Failed, result.Completion);
        Assert.True(result.OperationsDrained);
        Assert.True(result.MetricsCoverageComplete);
        Assert.True(result.OutputStopped);
        Assert.False(result.FlushConfirmed);
        Assert.False(result.DataComplete);
        Assert.Equal(TimeSpan.FromSeconds(10), result.FlushDuration);
    }

    [Fact]
    public async Task NoncooperativeTransportIsReportedAndPoolDisposalWaitsForTheActualOperation()
    {
        var time = new VirtualTime();
        var transport = new GatedTransport(time, 200, cooperative: false);
        var runner = Runner(time, transport);
        var pending = runner.RunAsync(Plan(rate: 1, timeoutSeconds: 2), new Output());
        await WaitUntil(() => runner.State == LocalRunState.Draining && time.HasTimerAfter(TimeSpan.FromSeconds(3)));
        time.Advance(TimeSpan.FromSeconds(3));
        await WaitUntil(() => time.HasTimerAfter(LocalRunController.ShutdownGrace));
        time.Advance(LocalRunController.ShutdownGrace);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunReason.DrainTimedOut, result.Reason);
        Assert.False(result.OperationsDrained);
        Assert.False(result.DataComplete);
        Assert.False(transport.Disposed.Task.IsCompleted);
        transport.Release.TrySetResult();
        await transport.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(transport.DisposedWhileActive);
    }

    [Fact]
    public async Task NoncooperativeFlushIsMarkedStillRunningAndLateFaultIsObserved()
    {
        var time = new VirtualTime();
        var entered = Signal();
        var release = Signal();
        var completed = Signal();
        var output = new Output
        {
            OnFlush = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                completed.TrySetResult();
                throw new IOException("synthetic late fault");
            }
        };
        var pending = Runner(time, new ImmediateTransport(time)).RunAsync(Plan(), output);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => time.HasTimerAfter(LocalRunController.FlushTimeout));
        time.Advance(LocalRunController.FlushTimeout);
        await WaitUntil(() => time.HasTimerAfter(LocalRunController.ShutdownGrace));
        time.Advance(LocalRunController.ShutdownGrace);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.OutputStopped);
        Assert.False(result.FlushConfirmed);
        Assert.Equal(LocalRunReason.FlushTimedOut, result.Reason);
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LocalRunCompletion.Failed, result.Completion);
    }

    [Fact]
    public async Task CancellationRacingEofNeverDuplicatesTerminalOrDisposesAnActivePool()
    {
        for (int repetition = 0; repetition < 10; repetition++)
        {
            var time = new VirtualTime();
            using var stop = new CancellationTokenSource();
            var transport = new GatedTransport(time, 200);
            var runner = Runner(time, transport);
            var pending = runner.RunAsync(Plan(rate: 1), new Output(), stop.Token);
            await WaitUntil(() => runner.State == LocalRunState.Draining);
            await Task.WhenAll(Task.Run(stop.Cancel), Task.Run(() => transport.Release.TrySetResult()));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.DataComplete);
            Assert.Equal(1UL, result.Totals.TerminalDuration.Count);
            Assert.Equal(1, result.Admission.Completed);
            Assert.Equal(0, result.Admission.InFlight);
            Assert.Contains(result.Completion, new[] { LocalRunCompletion.Completed, LocalRunCompletion.Cancelled });
            Assert.False(transport.DisposedWhileActive);
            Assert.Equal(LocalRunState.Finished, runner.State);
        }
    }

    [Theory]
    [InlineData(202)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task LifecycleIntegratesActualLoopbackHttpAndSeparatesProtocolFromTargetErrors(int status)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = status, RetryAfterValues = ["17"], BodyBytes = 32
        });
        var time = new VirtualTime();
        var output = new Output();
        // Real HTTP and fixture, simulated schedule: deterministic due without claiming host calibration.
        var runner = new LocalRunController(time, time, new AdvancingWait(time));
        var result = await runner.RunAsync(Plan(rate: 2, target: fixture.Address.ToString()), output)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, fixture.ReceivedCount);
        Assert.Equal(2, result.Admission.Started);
        Assert.Equal(2UL, result.Totals[MetricCounter.HttpCompleted]);
        Assert.Equal(status == 202 ? 2UL : 0UL, result.Totals[MetricCounter.HttpSuccess]);
        Assert.Equal(status == 429 ? 2UL : 0UL, result.Totals.StatusReceived[329]);
        Assert.Equal(status == 429 ? 2UL : 0UL, result.Totals.RetryAfterKinds[(int)RetryAfterKind.DeltaSeconds]);
        Assert.Equal(LocalRunCompletion.Completed, result.Completion);
        Assert.True(result.DataComplete);
        Assert.All(output.Written, window => Assert.Equal(MetricWindowCodec.Encode(window),
            MetricWindowCodec.Encode(MetricWindowCodec.Decode(MetricWindowCodec.Encode(window)))));
    }

    [Fact]
    public async Task EmptyTryReadDoesNotLoseTheWakeupOfAnAlreadyWaitingConsumer()
    {
        var time = new VirtualTime();
        var sink = new MetricWindowSink();
        var pending = sink.PeekAsync(default).AsTask();
        Assert.False(sink.TryRead(out _)); // Must not replace the signal on which PeekAsync is waiting.
        new MetricWindowCollector(time, 0, sink).Close();
        var window = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(window);
        Assert.Equal(1, sink.Count); // Peek preserves the reservation until acknowledgement.
        sink.Complete();
        sink.Acknowledge(window);
        Assert.Null(await sink.PeekAsync(default));
    }

    private static LocalRunController Runner(VirtualTime time, ILocalRunTransport transport, ISchedulerWait? wait = null) =>
        new(time, time, wait ?? new AdvancingWait(time), _ => transport);
    private static LoadTestPlan Plan(int rate = 10, int seconds = 1, int capacity = 4, int timeoutSeconds = 10,
        string target = "http://127.0.0.1:12345/") => LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = target, RequestsPerSecond = rate, DurationSeconds = seconds,
            MaxConcurrency = capacity, RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds)
        }, Guid.NewGuid()).Plan!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
    private sealed class AdvancingWait(VirtualTime time, Action<int>? after = null) : ISchedulerWait
    {
        private int calls;
        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            time.Advance(delay);
            after?.Invoke(++calls);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ImmediateTransport(VirtualTime time, Exception? fault = null) : ILocalRunTransport
    {
        public long Starts;
        public bool Disposed;
        public Task<RequestObservation> SendAsync(long index, CancellationToken token, Action<ResponseHeadersObservation>? headers)
        {
            Interlocked.Increment(ref Starts);
            if (fault is not null) return Task.FromException<RequestObservation>(fault);
            headers?.Invoke(new(index, 202, RetryAfterObservation.Parse(202, [], DateTimeOffset.UnixEpoch), time.GetTimestamp()));
            return Task.FromResult(new RequestObservation(index, RequestOutcome.HttpCompleted, TimeSpan.Zero, 202, 17));
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class GatedTransport(VirtualTime time, int status, bool cooperative = true) : ILocalRunTransport
    {
        public readonly TaskCompletionSource Release = Signal(), Disposed = Signal();
        public long Starts;
        public bool Cancelled, DisposedWhileActive;
        private int active;
        public async Task<RequestObservation> SendAsync(long index, CancellationToken token, Action<ResponseHeadersObservation>? headers)
        {
            Interlocked.Increment(ref Starts);
            Interlocked.Increment(ref active);
            long start = time.GetTimestamp();
            try
            {
                headers?.Invoke(new(index, status, RetryAfterObservation.Parse(status, [], DateTimeOffset.UnixEpoch), time.GetTimestamp()));
                try { await Release.Task.WaitAsync(cooperative ? token : CancellationToken.None); }
                catch (OperationCanceledException)
                {
                    Cancelled = true;
                    return new(index, RequestOutcome.CancelledInFlight, time.GetElapsedTime(start, time.GetTimestamp()),
                        status, 0, RequestErrorKind.Cancellation);
                }
                return new(index, RequestOutcome.HttpCompleted, time.GetElapsedTime(start, time.GetTimestamp()), status, 17);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public void Dispose()
        {
            DisposedWhileActive = Volatile.Read(ref active) != 0;
            Disposed.TrySetResult();
        }
    }
    private sealed class Output : ILocalMetricOutput
    {
        public readonly ConcurrentQueue<MetricWindow> Written = new();
        public Func<MetricWindow, CancellationToken, ValueTask>? OnWrite;
        public Func<CancellationToken, ValueTask>? OnFlush;
        public async ValueTask WriteAsync(MetricWindow window, CancellationToken token)
        {
            if (OnWrite is not null) await OnWrite(window, token);
            Written.Enqueue(window);
        }
        public async ValueTask FlushAsync(MetricWindowManifest? manifest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (OnFlush is not null) await OnFlush(token);
        }
    }
    private sealed class VirtualTime : TimeProvider, IMonotonicClock
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public bool HasTimerAfter(TimeSpan delay) { lock (gate) return timers.Any(t => t.DelayTicks == delay.Ticks && t.Due == ticks + delay.Ticks); }
        public void Advance(TimeSpan elapsed)
        {
            List<Timer> ready;
            lock (gate)
            {
                ticks = checked(ticks + elapsed.Ticks);
                ready = timers.Where(timer => timer.Due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = long.MaxValue;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (gate) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class Timer(VirtualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue, DelayTicks;
            internal TimerCallback Callback => callback;
            internal object? State => state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    DelayTicks = dueTime.Ticks;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.ticks + dueTime.Ticks);
                }
                return true;
            }
            public void Dispose() { lock (owner.gate) { Due = long.MaxValue; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
