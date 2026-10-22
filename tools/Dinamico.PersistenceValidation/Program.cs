using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Metrics;
using Dinamico.Persistence;

// Synthetic database experiment only. No HTTP traffic or inference about engine capacity.
string connection = Environment.GetEnvironmentVariable("DINAMICO_DATABASE_CONNECTION")
    ?? throw new InvalidOperationException("Set DINAMICO_DATABASE_CONNECTION to a dedicated validation database.");
if (args is not ["--output", var output])
    throw new ArgumentException("Usage: --output FILE (new evidence file; use an exclusive database)");
await using var evidence = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
var store = new PostgresRunStore(connection);
await store.MigrateAsync();
var results = new List<object>();
foreach (bool dense in new[] { false, true })
{
    var clock = new Clock();
    var sink = new MetricWindowSink();
    var collector = new MetricWindowCollector(clock, 0, sink);
    for (int i = 0; i < 1000; i++)
    {
        ulong nanoseconds = dense ? LogHistogramSchema.UpperBounds[i % LogHistogramSchema.UpperBounds.Length] : 1000000UL;
        collector.RecordStarted(0);
        collector.RecordHeaders(new(i, 200, RetryAfterObservation.Parse(200, null, DateTimeOffset.UnixEpoch), 0));
        collector.RecordTerminal(new(i, RequestOutcome.HttpCompleted,
            TimeSpan.FromTicks(checked((long)((nanoseconds + 99) / 100))), 200, 16), 0);
    }
    clock.Ticks = TimeSpan.TicksPerSecond;
    var manifest = collector.Close();
    if (!sink.TryRead(out var window)) throw new InvalidOperationException("Missing synthetic window.");
    Guid project = Guid.NewGuid();
    var input = new ExecutionImport(project, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow,
        new(1000, 1, 32, RequestTimeoutTicks: TimeSpan.FromSeconds(60).Ticks), [window!], true, 1000, 1000, 0, manifest);
    // Warmup uses another project and is explicitly excluded from measurement.
    await store.ImportAsync(input with { ProjectId = Guid.NewGuid() });
    var before = await store.MeasureAsync();
    var durations = new List<double>();
    for (int i = 0; i < 30; i++)
    {
        var timer = Stopwatch.StartNew();
        await store.ImportAsync(input with { ExecutionId = Guid.NewGuid() });
        durations.Add(timer.Elapsed.TotalMilliseconds);
    }
    var after = await store.MeasureAsync();
    durations.Sort();
    results.Add(new
    {
        population = dense ? "dense" : "sparse", projectId = project, executions = 30, windowsPerExecution = 1,
        requestsPerWindow = 1000, nonemptyBuckets = window!.TerminalDuration.Counts.ToArray().Count(v => v != 0),
        logicalWindowBytes = MetricWindowCodec.EncodedSize, before, after,
        relationGrowthBytes = after.RelationBytes - before.RelationBytes,
        clusterWalGrowthBytes = after.WalPositionBytes - before.WalPositionBytes,
        latencyMilliseconds = new { minimum = durations[0], median = durations[14], p95 = durations[28], maximum = durations[^1] }
    });
}
await JsonSerializer.SerializeAsync(evidence, new
{
    schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow,
    runtime = RuntimeInformation.FrameworkDescription, operatingSystem = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    databaseScope = "dedicated PostgreSQL; relation sizes include indexes/TOAST; WAL position covers the entire cluster",
    warmup = "one separate import per population; excluded from measurements",
    interpretation = "Synthetic local database experiment; not HTTP or distributed capacity. Page allocation, compression and WAL full-page images affect differences.",
    results
}, new JsonSerializerOptions { WriteIndented = true });

sealed class Clock : IMonotonicClock
{
    public long Ticks;
    public long GetTimestamp() => Ticks;
    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) => TimeSpan.FromTicks(endingTimestamp - startingTimestamp);
}
