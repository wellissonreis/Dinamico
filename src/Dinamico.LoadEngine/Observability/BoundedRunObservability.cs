using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Lifecycle;
using Dinamico.LoadEngine.Metrics;

namespace Dinamico.LoadEngine.Observability;

public readonly record struct ObservabilitySnapshot(long SamplesRecorded, long SamplesDiscarded,
    long EventsSuppressed, long LinesDropped, bool OutputFailed, int PendingLines);

/// <summary>Allowlisted, aggregate diagnostics. No request, configuration, exception or free text input.
/// The output pump owns at most one active write and a bounded queue; producers never await I/O.</summary>
public sealed class BoundedRunObservability
{
    public const int MaximumSamplesPerWindow = 5, MaximumSamplesPerRun = 100,
        MaximumSampleBytes = 512, MaximumLineBytes = 4096, QueueCapacity = 32;
    private readonly object gate = new();
    private readonly Guid executionId;
    private readonly TimeProvider time;
    private readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, (long Timestamp, long Suppressed)> fingerprints = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private readonly HttpRuntimeMetrics httpMetrics;
    private long samples, discarded, suppressed, dropped;
    private bool outputFailed, completed, started;
    private ulong? lastWindow;
    private bool? saturated;

    public BoundedRunObservability(TextWriter writer, Guid executionId, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        httpMetrics = new();
        this.executionId = executionId;
        time = timeProvider ?? TimeProvider.System;
        // Isolate even writers that block synchronously before returning their ValueTask.
        pump = Task.Run(async () =>
        {
            try
            {
                await foreach (string line in lines.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                    await writer.WriteLineAsync(line.AsMemory(), stop.Token).ConfigureAwait(false);
                await writer.FlushAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception) { lock (gate) outputFailed = true; }
        });
    }

    public ObservabilitySnapshot Snapshot
    {
        get { lock (gate) return new(samples, discarded, suppressed, dropped, outputFailed, lines.Reader.Count); }
    }

    public void Start(RateLimitMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (gate)
        {
            if (started || completed) return;
            started = true;
            Emit("run.started", "Information", "prepared", new { mode = mode.ToString(), retry = "None", success_rule = "Completed2xxV1" });
        }
    }

    public void ObserveWindow(MetricWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (gate)
        {
            if (completed || lastWindow is { } previous && window.WindowIndex <= previous) return;
            lastWindow = window.WindowIndex;
            int selected = 0;
            // Aggregate rare terminal categories before HTTP floods. One sample per category/status/window.
            Sample("internal", window[MetricCounter.InternalFailed]);
            Sample("transport", window[MetricCounter.TransportFailed]);
            Sample("request_timeout", window[MetricCounter.RequestTimedOut]);
            Sample("response_limit", window[MetricCounter.ResponseLimitExceeded]);
            Sample("cancelled", window[MetricCounter.CancelledInFlight]);
            for (int status = 400; status <= 599; status++) Sample("http_status", window.StatusReceived[status - 100], status);
            bool limited = window[MetricCounter.ConcurrencyLimit] != 0 || window[MetricCounter.SchedulerLate] != 0;
            if (saturated != limited)
            {
                if (limited || saturated.HasValue)
                    EmitLimited("generator.capacity_changed", limited ? "Warning" : "Information",
                        limited ? "generator_limited" : "generator_recovered", new { window_index = window.WindowIndex });
                saturated = limited;
            }
            // Fixed frequency follows immutable five-second metric windows, never request completion frequency.
            long? workingSet = null, cpuTicks = null, heap = null, allocated = null, gcHeap = null,
                fragmented = null, gcPauseTicks = null, handles = null;
            int? gen0 = null, gen1 = null, gen2 = null;
            try
            {
                using var process = Process.GetCurrentProcess();
                workingSet = process.WorkingSet64;
                cpuTicks = process.TotalProcessorTime.Ticks;
                // The approximate runtime sample can race a collection and be invalid.
                // Unknown is preferable to publishing negative retained bytes or a fabricated zero.
                long measuredHeap = GC.GetTotalMemory(false);
                heap = measuredHeap >= 0 ? measuredHeap : null;
                allocated = GC.GetTotalAllocatedBytes(precise: false);
                var gc = GC.GetGCMemoryInfo();
                gcHeap = gc.HeapSizeBytes;
                fragmented = gc.FragmentedBytes;
                gcPauseTicks = GC.GetTotalPauseDuration().Ticks;
                gen0 = GC.CollectionCount(0);
                gen1 = GC.CollectionCount(1);
                gen2 = GC.CollectionCount(2);
                handles = process.HandleCount;
            }
            catch (Exception) { /* Unsupported runtime measurements remain unknown. */ }
            var http = httpMetrics.Snapshot;
            Emit("process.summary", "Information", "window_closed", new
            {
                window_index = window.WindowIndex, elapsed_ticks = window.End.Ticks, in_flight = window.InFlight,
                concurrency_limit = window[MetricCounter.ConcurrencyLimit], scheduler_late = window[MetricCounter.SchedulerLate],
                request_timeouts = window[MetricCounter.RequestTimedOut], working_set_bytes = workingSet,
                cpu_time_ticks = cpuTicks, managed_heap_bytes = heap,
                gc_heap_size_bytes = gcHeap, gc_fragmented_bytes = fragmented,
                gc_total_allocated_bytes = allocated, gc_total_pause_ticks = gcPauseTicks,
                gc_collection_count_gen0 = gen0, gc_collection_count_gen1 = gen1, gc_collection_count_gen2 = gen2,
                process_handle_count = handles, thread_pool_pending_work_items = ThreadPool.PendingWorkItemCount,
                thread_pool_thread_count = ThreadPool.ThreadCount,
                http_open_connections_current = http.OpenConnectionsCurrent, http_open_connections_peak = http.OpenConnectionsPeak,
                http_active_requests_current = http.ActiveRequestsCurrent, http_active_requests_peak = http.ActiveRequestsPeak,
                http_queue_count = http.QueueCount, http_queue_total_seconds = http.QueueTotalSeconds,
                http_queue_max_seconds = http.QueueMaxSeconds,
                events_suppressed = suppressed, samples_discarded = discarded, lines_dropped = dropped
            });

            void Sample(string category, ulong count, int? status = null)
            {
                if (count == 0) return;
                if (selected == MaximumSamplesPerWindow || samples == MaximumSamplesPerRun) { discarded++; return; }
                var data = new { window_index = window.WindowIndex, category, status, occurrences = count };
                if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(data)) > MaximumSampleBytes) { discarded++; return; }
                if (EmitLimited("error.sample", "Information", category, data, status))
                {
                    selected++;
                    samples++;
                }
            }
        }
    }

    public void Finish(LocalRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (gate)
        {
            if (completed) return;
            foreach (var transition in result.Transitions)
                Emit("run.state_changed", "Information", transition.State.ToString(), new { elapsed_ticks = transition.Elapsed.Ticks });
            Emit("run.stopped", result.Completion == LocalRunCompletion.Failed ? "Error" : "Information", result.Reason.ToString(), new
            {
                completion = result.Completion.ToString(), data_complete = result.DataComplete,
                flush_confirmed = result.FlushConfirmed, failures = (int)result.Failures,
                samples_recorded = samples, samples_discarded = discarded, events_suppressed = suppressed, lines_dropped = dropped
            });
        }
    }

    /// <summary>Stops within the caller's bound even when a writer ignores cancellation. The caller owns the writer.</summary>
    public async Task<bool> CompleteAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (gate) { completed = true; lines.Writer.TryComplete(); }
        try { await pump.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); return !Snapshot.OutputFailed; }
        catch (TimeoutException) { StopOutput(); return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { StopOutput(); return false; }
        finally { httpMetrics.Dispose(); }
    }

    private void StopOutput()
    {
        lock (gate) outputFailed = true;
        _ = CancelOutputAsync();
    }

    private async Task CancelOutputAsync()
    {
        try { await stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* Writer cancellation callbacks may fail; never expose their text. */ }
    }

    private bool EmitLimited(string name, string level, string reason, object data, int? status = null)
    {
        string key = name + ":" + reason + ":" + status;
        long now = time.GetTimestamp();
        if (fingerprints.TryGetValue(key, out var previous)
            && time.GetElapsedTime(previous.Timestamp, now) < TimeSpan.FromSeconds(30))
        {
            fingerprints[key] = (previous.Timestamp, previous.Suppressed + 1);
            suppressed++;
            return false;
        }
        fingerprints[key] = (now, 0);
        Emit(name, level, reason, new { suppressed = previous.Suppressed, detail = data });
        return true;
    }

    private void Emit(string name, string level, string reason, object data)
    {
        string line = JsonSerializer.Serialize(new
        {
            timestamp = time.GetUtcNow(), level, @event = name, execution_id = executionId,
            schema_version = 1, reason_code = reason, truncated = false, data
        });
        if (outputFailed || Encoding.UTF8.GetByteCount(line) > MaximumLineBytes || !lines.Writer.TryWrite(line)) dropped++;
    }
}
