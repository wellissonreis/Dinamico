using System.Collections.Immutable;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Scheduling;
using Dinamico.LoadEngine.Timing;
using Dinamico.LoadEngine.Transport;

namespace Dinamico.LoadEngine.Lifecycle;

/// <summary>Owns one local execution and its HTTP pool. Output ownership stays with the caller.</summary>
public sealed class LocalRunController
{
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(1);
    private readonly IMonotonicClock clock;
    private readonly TimeProvider time;
    private readonly ISchedulerWait wait;
    private readonly Func<LoadTestPlan, ILocalRunTransport> createTransport;
    private int used, state;

    public LocalRunController() : this(StopwatchMonotonicClock.Instance, TimeProvider.System, new SchedulerWait()) { }

    internal LocalRunController(IMonotonicClock clock, TimeProvider time, ISchedulerWait wait,
        Func<LoadTestPlan, ILocalRunTransport>? createTransport = null)
    {
        this.clock = clock;
        this.time = time;
        this.wait = wait;
        this.createTransport = createTransport ?? (plan => new LocalHttpTransport(plan, clock, time));
    }

    public LocalRunState State => (LocalRunState)Volatile.Read(ref state);

    public Task<LocalRunResult> RunAsync(LoadTestPlan plan, ILocalMetricOutput output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);
        if (plan.SchemaVersion != 2 || plan.RetryPolicy != LoadRetryPolicy.None
            || plan.SuccessRule != HttpSuccessRule.Completed2xxV1 || plan.HistogramSchema != LogHistogramSchema.Name)
            throw new ArgumentException("Unsupported local plan contract.", nameof(plan));
        if (Interlocked.Exchange(ref used, 1) != 0)
            throw new InvalidOperationException("A local controller can run only once.");
        return new Session(this, plan, output).RunAsync(cancellationToken);
    }

    private sealed class Session
    {
        private readonly LocalRunController owner;
        private readonly LoadTestPlan plan;
        private readonly ILocalMetricOutput output;
        private readonly object gate = new();
        private readonly MetricWindowSink sink = new();
        private readonly MetricWindowCollector metrics;
        private readonly ArrivalSchedule schedule;
        private readonly ArrivalScheduler scheduler;
        private readonly BoundedAdmission admission;
        private readonly CancellationTokenSource abort = new(), monitorStop = new(), outputStop = new();
        private readonly List<LocalRunTransition> transitions = new(5);
        private readonly long t0;
        private ILocalRunTransport? transport;
        private LocalRunReason? reason;
        private LocalRunFailure failures;
        private TimeSpan? generationEnd;
        private long classifiedDue;
        private bool metricsFailed, acceptMetrics = true, operationsDone, finished, outputFailed;
        private Task abortCallbacks = Task.CompletedTask;
        private MetricWindowTotals totals = MetricWindowTotals.Empty;

        internal Session(LocalRunController owner, LoadTestPlan plan, ILocalMetricOutput output)
        {
            this.owner = owner;
            this.plan = plan;
            this.output = output;
            t0 = owner.clock.GetTimestamp();
            metrics = new(owner.clock, t0, sink);
            schedule = ArrivalSchedule.FromPlan(plan);
            scheduler = new(schedule, owner.clock, t0, owner.wait);
            admission = new(plan.MaxConcurrency, SendAsync, ObserveTerminal, owner.clock);
            transitions.Add(new(LocalRunState.Prepared, TimeSpan.Zero));
        }

        internal async Task<LocalRunResult> RunAsync(CancellationToken token)
        {
            Task consume = ConsumeAsync();
            Task outputWork = consume;
            Task monitor = Task.CompletedTask;
            Task drain = Task.CompletedTask;
            bool drained = false, flushConfirmed = false, outputStopped = false;
            TimeSpan operationsEnd = TimeSpan.Zero, flushStart = TimeSpan.Zero;
            MetricWindowManifest? manifest = null;
            using var registration = token.UnsafeRegister(static state => ((Session)state!).Stop(LocalRunReason.UserCancelled), this);
            try
            {
                try
                {
                    lock (gate)
                    {
                        if (reason is null)
                        {
                            transport = owner.createTransport(plan);
                            Transition(LocalRunState.Generating);
                        }
                    }
                    if (owner.State == LocalRunState.Generating)
                    {
                        monitor = MonitorAsync();
                        await GenerateAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (metrics.IsFaulted) MetricFailure(ex);
                    else Stop(LocalRunReason.InternalFailure);
                }
                finally
                {
                    lock (gate)
                    {
                        EndGeneration();
                        long due = schedule.CountDue(generationEnd!.Value);
                        long remaining = checked(due - classifiedDue);
                        if (remaining < 0) throw new InvalidOperationException("Scheduled due accounting exceeded stop boundary.");
                        if (remaining != 0 && !metricsFailed)
                        {
                            try
                            {
                                metrics.RecordNotStarted(reason is null ? NotStartedReason.SchedulerLate : NotStartedReason.Policy,
                                    (ulong)remaining, owner.clock.GetTimestamp());
                                classifiedDue = due;
                            }
                            catch (Exception ex) { MetricFailure(ex); }
                        }
                    }
                }

                drain = admission.DrainAsync();
                try
                {
                    await drain.WaitAsync(plan.RequestTimeout + ShutdownGrace, owner.time, abort.Token).ConfigureAwait(false);
                    drained = true;
                }
                catch (OperationCanceledException) when (abort.IsCancellationRequested)
                {
                    drained = await FinishAbortedDrainAsync(drain).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Stop(LocalRunReason.DrainTimedOut);
                    drained = await FinishAbortedDrainAsync(drain).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Stop(LocalRunReason.InternalFailure);
                    drained = admission.Snapshot.InFlight == 0;
                }
                try { await abortCallbacks.WaitAsync(ShutdownGrace, owner.time).ConfigureAwait(false); }
                catch (Exception) { Stop(LocalRunReason.InternalFailure); }
                lock (gate) { operationsDone = true; acceptMetrics = false; operationsEnd = Elapsed(); }
                monitorStop.Cancel();
                await monitor.ConfigureAwait(false); // Monitor owns/categorizes its exceptions.
                if (drained)
                {
                    try { transport?.Dispose(); }
                    catch (Exception) { Stop(LocalRunReason.InternalFailure); }
                    transport = null;
                }
                if (drained && !metricsFailed)
                {
                    try { manifest = metrics.Close(); }
                    catch (Exception ex) { MetricFailure(ex); }
                }
                manifest ??= metrics.Manifest;
                sink.Complete();
                flushStart = Elapsed();
                Task<bool> flush = FlushAsync(consume, manifest);
                outputWork = flush;
                try
                {
                    flushConfirmed = await flush.WaitAsync(FlushTimeout, owner.time).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Stop(LocalRunReason.FlushTimedOut);
                    ObserveLater(outputStop.CancelAsync());
                    try { await flush.WaitAsync(ShutdownGrace, owner.time).ConfigureAwait(false); }
                    catch (TimeoutException) { ObserveLater(flush); }
                }
                outputStopped = flush.IsCompleted;

                MetricWindowTotals resultTotals;
                lock (gate)
                {
                    resultTotals = totals;
                    foreach (var window in sink.Snapshot())
                    {
                        if (window.WindowIndex < resultTotals.WindowCount) continue;
                        try { resultTotals = resultTotals.Append(window); }
                        catch (Exception) { metricsFailed = true; Stop(LocalRunReason.InternalFailure); break; }
                    }
                    long due = schedule.CountDue(generationEnd!.Value);
                    bool coverage = manifest is { } proof && resultTotals.HasCompleteCoverage(proof)
                        && !metricsFailed && resultTotals[MetricCounter.ScheduledDue] == (ulong)due
                        && resultTotals[MetricCounter.Started] == (ulong)admission.Snapshot.Started;
                    bool dataComplete = drained && coverage && flushConfirmed && outputStopped;
                    var quality = metrics.Quality | resultTotals.Quality;
                    if (!dataComplete) quality |= MeasurementQuality.MetricsPartial;
                    var completion = failures != LocalRunFailure.None ? LocalRunCompletion.Failed
                        : reason == LocalRunReason.UserCancelled ? LocalRunCompletion.Cancelled : LocalRunCompletion.Completed;
                    if (!dataComplete && completion == LocalRunCompletion.Completed)
                    {
                        Stop(LocalRunReason.InternalFailure);
                        completion = LocalRunCompletion.Failed;
                    }
                    finished = true;
                    Transition(LocalRunState.Finished);
                    TimeSpan end = Elapsed();
                    return new(plan.ExecutionId, completion, reason ?? LocalRunReason.DurationElapsed, failures,
                        plan.PlannedTotal, due, checked(plan.PlannedTotal - due), generationEnd.Value,
                        operationsEnd - generationEnd.Value, end - flushStart, end, admission.Snapshot,
                        resultTotals, manifest, quality, drained, coverage, flushConfirmed, outputStopped,
                        ImmutableArray.CreateRange(transitions));
                }
            }
            finally
            {
                monitorStop.Cancel();
                // Structural failures still stop admissions/observe I/O before disposing the pool.
                if (!finished) Stop(LocalRunReason.InternalFailure);
                if (!drained)
                {
                    admission.StopAdmissions();
                    var outstanding = admission.DrainAsync();
                    ObserveLater(CleanupAfterDrainAsync(outstanding));
                }
                else
                {
                    ObserveLater(DisposeAbortAsync());
                }
                if (outputStopped) outputStop.Dispose();
                else
                {
                    ObserveLater(outputStop.CancelAsync());
                    ObserveLater(CleanupOutputAsync(outputWork));
                }
                monitorStop.Dispose();
            }
        }

        private async Task GenerateAsync()
        {
            while (true)
            {
                abort.Token.ThrowIfCancellationRequested();
                var wake = await scheduler.WaitForNextWakeAsync(abort.Token).ConfigureAwait(false);
                lock (gate)
                {
                    try
                    {
                        if (reason is not null || wake.IsCompleted || Elapsed() >= schedule.Duration) return;
                        if (wake.SchedulerLate != 0)
                        {
                            classifiedDue = checked(classifiedDue + wake.SchedulerLate);
                            metrics.RecordNotStarted(NotStartedReason.SchedulerLate, (ulong)wake.SchedulerLate, owner.clock.GetTimestamp());
                        }
                        for (long offset = 0; offset < wake.ArrivalCount; offset++)
                        {
                            if (reason is not null || Elapsed() >= schedule.Duration) return;
                            classifiedDue = checked(classifiedDue + 1);
                            if (!admission.TryStart(wake[offset], abort.Token, out var rejection))
                                metrics.RecordNotStarted(rejection!.Value, 1, owner.clock.GetTimestamp());
                        }
                    }
                    catch (Exception ex)
                    {
                        if (metrics.IsFaulted) MetricFailure(ex);
                        else Stop(LocalRunReason.InternalFailure);
                        return;
                    }
                }
            }
        }

        private async Task<RequestObservation> SendAsync(long index, CancellationToken token)
        {
            try { metrics.RecordStarted(owner.clock.GetTimestamp()); }
            catch (Exception ex) { MetricFailure(ex); throw; }
            try { return await transport!.SendAsync(index, token, ObserveHeaders).ConfigureAwait(false); }
            catch (TransportObserverException ex) { MetricFailure(ex.InnerException ?? ex); throw; }
            catch (Exception) { Stop(LocalRunReason.InternalFailure); throw; }
        }
        private void ObserveHeaders(ResponseHeadersObservation observation)
        {
            lock (gate)
            {
                if (!acceptMetrics || metricsFailed) return;
                try { metrics.RecordHeaders(observation); }
                catch (Exception ex) { MetricFailure(ex); throw; }
            }
        }
        private void ObserveTerminal(RequestObservation observation)
        {
            lock (gate)
            {
                if (acceptMetrics && !metricsFailed)
                {
                    try { metrics.RecordTerminal(observation, owner.clock.GetTimestamp()); }
                    catch (Exception ex) { MetricFailure(ex); }
                }
                if (observation.Outcome == RequestOutcome.InternalFailed && !metricsFailed)
                    Stop(LocalRunReason.InternalFailure);
            }
        }
        private async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(MetricWindow.Width, owner.time, monitorStop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (operationsDone || metricsFailed) return;
                        try { metrics.Advance(); }
                        catch (Exception ex) { MetricFailure(ex); return; }
                    }
                }
            }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            catch (Exception ex) { MetricFailure(ex); }
        }
        private async Task ConsumeAsync()
        {
            try
            {
                while (await sink.PeekAsync(outputStop.Token).ConfigureAwait(false) is { } window)
                {
                    lock (gate) totals = totals.Append(window);
                    try { await output.WriteAsync(window, outputStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (outputStop.IsCancellationRequested) { return; }
                    catch (Exception)
                    {
                        lock (gate) { outputFailed = true; Stop(LocalRunReason.OutputFailure); }
                        return;
                    }
                    sink.Acknowledge(window);
                }
            }
            catch (OperationCanceledException) when (outputStop.IsCancellationRequested) { }
            catch (Exception) { lock (gate) metricsFailed = true; Stop(LocalRunReason.InternalFailure); }
        }
        private async Task<bool> FlushAsync(Task consume, MetricWindowManifest? manifest)
        {
            await consume.ConfigureAwait(false);
            lock (gate) if (outputFailed) return false;
            try { await output.FlushAsync(manifest, outputStop.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) when (outputStop.IsCancellationRequested) { return false; }
            catch (Exception) { Stop(LocalRunReason.OutputFailure); return false; }
        }
        private async Task<bool> FinishAbortedDrainAsync(Task drain)
        {
            try { await drain.WaitAsync(ShutdownGrace, owner.time).ConfigureAwait(false); return true; }
            catch (TimeoutException) { Stop(LocalRunReason.DrainTimedOut); ObserveLater(drain); return false; }
            catch (Exception) { Stop(LocalRunReason.InternalFailure); return admission.Snapshot.InFlight == 0; }
        }
        private void MetricFailure(Exception error)
        {
            lock (gate)
            {
                metricsFailed = true;
                Stop(error is MetricBackpressureException ? LocalRunReason.TelemetryBackpressure : LocalRunReason.InternalFailure);
            }
        }
        private void Stop(LocalRunReason stopReason)
        {
            lock (gate)
            {
                if (finished || (stopReason == LocalRunReason.UserCancelled && operationsDone)) return;
                failures |= stopReason switch
                {
                    LocalRunReason.TelemetryBackpressure => LocalRunFailure.TelemetryBackpressure,
                    LocalRunReason.InternalFailure => LocalRunFailure.Internal,
                    LocalRunReason.OutputFailure => LocalRunFailure.Output,
                    LocalRunReason.FlushTimedOut => LocalRunFailure.FlushTimeout,
                    LocalRunReason.DrainTimedOut => LocalRunFailure.DrainTimeout,
                    _ => LocalRunFailure.None
                };
                reason ??= stopReason;
                EndGeneration();
                Transition(LocalRunState.Stopping);
                if (!abort.IsCancellationRequested) abortCallbacks = abort.CancelAsync();
            }
        }
        private void EndGeneration()
        {
            generationEnd ??= Elapsed();
            admission.StopAdmissions();
            if (reason is null) Transition(LocalRunState.Draining);
        }
        private void Transition(LocalRunState next)
        {
            if (owner.State == next) return;
            Volatile.Write(ref owner.state, (int)next);
            transitions.Add(new(next, Elapsed()));
        }
        private TimeSpan Elapsed()
        {
            var elapsed = owner.clock.GetElapsedTime(t0, owner.clock.GetTimestamp());
            if (elapsed < TimeSpan.Zero) throw new InvalidOperationException("Monotonic clock moved backwards.");
            return elapsed;
        }
        private async Task CleanupAfterDrainAsync(Task drain)
        {
            try { await drain.ConfigureAwait(false); } catch (Exception) { }
            try { transport?.Dispose(); } catch (Exception) { }
            try { await abortCallbacks.ConfigureAwait(false); } catch (Exception) { }
            abort.Dispose();
        }
        private async Task DisposeAbortAsync()
        {
            try { await abortCallbacks.ConfigureAwait(false); } catch (Exception) { }
            abort.Dispose();
        }
        private async Task CleanupOutputAsync(Task consume)
        {
            try { await consume.ConfigureAwait(false); } catch (Exception) { }
            outputStop.Dispose();
        }
        private static void ObserveLater(Task task) => _ = task.ContinueWith(static done => { _ = done.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
