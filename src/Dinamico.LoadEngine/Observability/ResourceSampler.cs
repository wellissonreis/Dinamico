using System.Diagnostics;

namespace Dinamico.LoadEngine.Observability;

/// <summary>Process-wide generator resources. HTTP deltas include all traffic in this process.</summary>
public sealed class ResourceSampler : IDisposable
{
    private readonly HttpRuntimeMetrics http = new();
    private long previousTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan? previousCpu = ReadCpu();

    public ResourceSample Sample(ulong index, double elapsedSeconds)
    {
        long? workingSet = null, heap = null, allocated = null, pauses = null;
        int? gen0 = null, gen1 = null, gen2 = null;
        double? cpu = null;
        long now = Stopwatch.GetTimestamp();
        TimeSpan? totalCpu = ReadCpu();
        if (totalCpu is { } current && previousCpu is { } previous && now > previousTimestamp)
            cpu = Math.Max(0, (current - previous).TotalSeconds / Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds * 100);
        previousCpu = totalCpu;
        previousTimestamp = now;
        try
        {
            using var process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
            long measuredHeap = GC.GetTotalMemory(false);
            heap = measuredHeap >= 0 ? measuredHeap : null;
            allocated = GC.GetTotalAllocatedBytes(false);
            pauses = GC.GetTotalPauseDuration().Ticks;
            gen0 = GC.CollectionCount(0); gen1 = GC.CollectionCount(1); gen2 = GC.CollectionCount(2);
        }
        catch (Exception) { /* Unsupported measurements remain unknown. */ }
        var network = http.Snapshot;
        return new(index, elapsedSeconds, workingSet, heap, cpu, allocated, pauses, gen0, gen1, gen2,
            ThreadPool.PendingWorkItemCount, ThreadPool.ThreadCount, network.OpenConnectionsCurrent,
            network.QueueMaxSeconds * 1000);
    }

    private static TimeSpan? ReadCpu()
    {
        try { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime; }
        catch (Exception) { return null; }
    }
    public void Dispose() => http.Dispose();
}
