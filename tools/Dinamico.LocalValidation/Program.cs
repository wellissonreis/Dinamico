using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Dinamico.LoadEngine.Tests.Fixtures;

using var interrupt = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; interrupt.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Console.WriteLine("Usage: Dinamico.LocalValidation [repository] [--calibrate] [--quick] [--require-gates] " +
            "[--durations 30,120,600] [--repetitions 3] [--rps 10,100] [--capture-mode Off|Sampled|Complete] [--output directory]\n" +
            "Default: smoke, 10/100 RPS for 30s. Calibration: separate 10s warmup, three 30s runs per rate, " +
            "then one 120s and one 600s run per rate, sequentially. Use --repetitions to repeat every selected duration. " +
            "Quick: separate 2s warmup and two 5s runs per rate. Functional failures always return 1; " +
            "--require-gates also fails on any capacity gate failure. Build the Release local CLI first.");
        return;
    }
    var root = new DirectoryInfo(Path.GetFullPath(options.Repository));
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "Dinamico.slnx"))) root = root.Parent;
    string repo = root?.FullName ?? throw new ArgumentException("Run inside the repository or pass its path.");
    string mode = options.Calibrate ? "Calibration" : options.Quick ? "Quick" : "Smoke";
    string evidence = options.Output is null
        ? Path.Combine(repo, "TestResults", "Ficha012", mode, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"))
        : Path.GetFullPath(options.Output);
    Directory.CreateDirectory(evidence);
    if (File.Exists(Path.Combine(evidence, "manifest.json")))
        throw new IOException("Evidence directory already contains a manifest; choose a new --output directory.");
    string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath ?? "dotnet";
    // An apphost cannot launch another managed DLL; use dotnet next to it instead.
    if (!Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        host = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    string engine = Path.Combine(repo, "src", "Dinamico.LoadEngine.Local", "bin", "Release", "net10.0", "Dinamico.LoadEngine.Local.dll");
    if (!File.Exists(engine)) throw new FileNotFoundException("Build the Release local CLI before validation.", engine);
    var scenarios = options.Scenarios().ToArray();
    if (options.CaptureMode == "Complete" && scenarios.Any(s => (long)s.Rps * s.DurationSeconds > 2000))
        throw new ArgumentException("Complete capture requires at most 2000 planned requests per scenario.");
    await WriteJson(Path.Combine(evidence, "manifest.json"), new
    {
        schemaVersion = 1, createdUtc = DateTimeOffset.UtcNow, mode, options.RequireGates, options.CaptureMode,
        environment = new { runtime = RuntimeInformation.FrameworkDescription, operatingSystem = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
            stopwatchFrequency = Stopwatch.Frequency, stopwatchHighResolution = Stopwatch.IsHighResolution },
        binaries = new[] { engine, Path.Combine(Path.GetDirectoryName(engine)!, "Dinamico.LoadEngine.dll") }
            .Select(path => new { name = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray(),
        execution = "Sequential isolated generator processes against one local controlled HTTP 202 fixture",
        warmupScope = "Separate process warms the target and OS caches; measured generators retain cold process/JIT startup. No hidden traffic is sent.",
        resourceScope = "Generator process only; fixture shares the machine, not the process",
        scenarios, gates = new { functional = "Exit 0, complete/clean coverage, fixture and terminal/status reconciliation",
            capacity = "Functional pass; zero drops; achieved started RPS within 2% of configured RPS; quality clear" },
        memoryInterpretation = "Trends are descriptive; no plateau criterion or steady-state claim is made."
    });
    Console.WriteLine(JsonSerializer.Serialize(new { evidence, mode, scenarioCount = scenarios.Length }));
    await using var fixture = await ControlledHttpFixture.StartAsync(new() { StatusCode = 202 });
    var summaries = new List<RunSummary>();
    foreach (var scenario in scenarios)
    {
        interrupt.Token.ThrowIfCancellationRequested();
        var summary = await Run(scenario, fixture, host, engine, evidence, options.CaptureMode, interrupt.Token);
        summaries.Add(summary);
        await WriteJson(Path.Combine(evidence, scenario.Name + ".summary.json"), summary);
        Console.WriteLine(JsonSerializer.Serialize(new { summary.Scenario, summary.FunctionalPassed, summary.NonSaturatedGatePassed,
            summary.ExitCode, summary.TimedOut, summary.Planned, summary.Started, summary.Drops, summary.AchievedRps, summary.Error }));
    }
    var measured = summaries.Where(s => !s.Scenario.Warmup).ToArray();
    bool functionalPassed = summaries.All(s => s.FunctionalPassed);
    bool nonSaturatedGatePassed = measured.All(s => s.NonSaturatedGatePassed);
    var aggregate = new
    {
        schemaVersion = 1, mode, functionalPassed, nonSaturatedGatePassed, options.RequireGates,
        warmupExcludedFromVariability = true,
        groups = measured.GroupBy(s => new { s.Scenario.Rps, s.Scenario.DurationSeconds }).Select(g => new
        {
            g.Key.Rps, g.Key.DurationSeconds, repetitions = g.Count(),
            functionalPassed = g.All(s => s.FunctionalPassed), nonSaturatedGatePassed = g.All(s => s.NonSaturatedGatePassed),
            achievedRps = Statistics(g.Select(s => s.AchievedRps)),
            droppedPercent = Statistics(g.Select(s => s.Planned > 0 ? 100d * s.Drops / s.Planned : (double?)null)),
            p95Nanoseconds = Statistics(g.Select(s => s.P95Nanoseconds)),
            peakRssBytes = Statistics(g.Select(s => s.Resources.Metrics.GetValueOrDefault("working_set_bytes")?.Maximum)),
            peakManagedHeapBytes = Statistics(g.Select(s => s.Resources.Metrics.GetValueOrDefault("managed_heap_bytes")?.Maximum))
        }).ToArray(),
        capacityConclusion = nonSaturatedGatePassed
            ? "All measured scenarios passed the stated gates on this host; this does not establish maximum capacity."
            : "At least one measured scenario failed a capacity gate; inspect per-run invariants and evidence.",
        memoryConclusion = "No plateau claim: inspect resource series, sample coverage and descriptive trends.", runs = summaries
    };
    await WriteJson(Path.Combine(evidence, "summary.json"), aggregate);
    Console.WriteLine(JsonSerializer.Serialize(new { functionalPassed, nonSaturatedGatePassed, evidence }));
    if (!functionalPassed || options.RequireGates && !nonSaturatedGatePassed) Environment.ExitCode = 1;
}
catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
{
    Console.Error.WriteLine("Validation cancelled; active generator stopped. Only completed per-run evidence is available.");
    Environment.ExitCode = 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Validation failed: {ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}
finally { Console.CancelKeyPress -= cancel; }

static async Task<RunSummary> Run(Scenario scenario, ControlledHttpFixture fixture, string host, string engine, string evidence, string captureMode,
    CancellationToken cancellationToken)
{
    string prefix = Path.Combine(evidence, scenario.Name);
    string config = prefix + ".config.json", report = prefix + ".report.json";
    await WriteJson(config, new { schemaVersion = 2, url = fixture.Address.ToString(), requestsPerSecond = scenario.Rps,
        durationSeconds = scenario.DurationSeconds, maxConcurrency = 32, requestTimeout = "00:00:02", captureMode });
    long before = fixture.ReceivedCount, connectionsBefore = fixture.ConnectionCount;
    var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string argument in new[] { engine, "run", "--config", config, "--output", report, "--overwrite" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local CLI.");
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    bool timedOut = false;
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(scenario.DurationSeconds + 60));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        timedOut = true;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    cancellationToken.ThrowIfCancellationRequested();
    string logs = await stderr.WaitAsync(TimeSpan.FromSeconds(10));
    await File.WriteAllTextAsync(prefix + ".jsonl", logs);
    await File.WriteAllTextAsync(prefix + ".stdout.txt", await stdout.WaitAsync(TimeSpan.FromSeconds(10)));
    var resources = ReadResources(logs);
    await WriteJson(prefix + ".resources.json", resources);
    var summary = new RunSummary(scenario, process.ExitCode, timedOut, fixture.ReceivedCount - before,
        fixture.ConnectionCount - connectionsBefore, resources);
    if (!File.Exists(report))
    {
        summary.Error = "No completed report was published.";
        return summary;
    }
    try
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var result = json.RootElement.GetProperty("result");
        var totals = result.GetProperty("totals");
        var counters = totals.GetProperty("counters");
        long Counter(string name) => counters.GetProperty(name).GetInt64();
        summary.Planned = result.GetProperty("plannedTotal").GetInt64();
        summary.Started = Counter("Started");
        summary.Drops = Counter("SchedulerLate") + Counter("ConcurrencyLimit") + Counter("Policy");
        double generation = result.GetProperty("durationsSeconds").GetProperty("generation").GetDouble();
        summary.AchievedRps = generation > 0 ? summary.Started / generation : null;
        var p95 = totals.GetProperty("httpCompletedDuration").GetProperty("p95");
        summary.P95Nanoseconds = p95.ValueKind == JsonValueKind.Object
            && p95.TryGetProperty("upperBoundNanoseconds", out var bound) && bound.ValueKind == JsonValueKind.Number
            ? bound.GetDouble() : null;
        long terminal = new[] { "HttpCompleted", "TransportFailed", "RequestTimedOut", "ResponseLimitExceeded", "CancelledInFlight", "InternalFailed" }.Sum(Counter);
        var statuses = totals.GetProperty("statuses");
        long http202 = statuses.TryGetProperty("202", out var status) ? status.GetInt64() : 0;
        var coverage = result.GetProperty("coverage");
        summary.Invariants = new()
        {
            ["processSucceeded"] = !timedOut && process.ExitCode == 0,
            ["dataComplete"] = result.GetProperty("dataComplete").GetBoolean(),
            ["coverageComplete"] = new[] { "operationsDrained", "metricsCoverageComplete", "flushConfirmed", "outputStopped" }
                .All(name => coverage.GetProperty(name).GetBoolean()),
            ["planMatchesConfiguration"] = summary.Planned == (long)scenario.Rps * scenario.DurationSeconds,
            ["scheduledMatchesPlan"] = Counter("ScheduledDue") == summary.Planned && result.GetProperty("notScheduledAfterStop").GetInt64() == 0,
            ["plannedEqualsStartedPlusDrops"] = summary.Planned == summary.Started + summary.Drops,
            ["startedEqualsTerminal"] = summary.Started == terminal,
            ["fixtureEqualsStarted"] = summary.Received == summary.Started,
            ["allStartedCompleted202"] = Counter("HttpCompleted") == summary.Started && Counter("HttpSuccess") == summary.Started && http202 == summary.Started,
            ["noInFlight"] = totals.GetProperty("lastInFlight").GetInt64() == 0 && fixture.ActiveRequests == 0
        };
        summary.FunctionalPassed = summary.Invariants.Values.All(value => value);
        summary.NonSaturatedGatePassed = summary.FunctionalPassed && summary.Drops == 0
            && summary.AchievedRps is { } achieved && Math.Abs(achieved - scenario.Rps) <= scenario.Rps * 0.02
            && IsClearQuality(result.GetProperty("quality"));
        summary.SanitizedPlanSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("plan").GetRawText())));
        summary.Environment = json.RootElement.GetProperty("environment").Clone();
        summary.Result = result.Clone();
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
    {
        summary.FunctionalPassed = false;
        summary.NonSaturatedGatePassed = false;
        summary.Error = $"Report could not be reconciled: {ex.GetType().Name}: {ex.Message}";
    }
    return summary;
}

static bool IsClearQuality(JsonElement quality) => quality.ValueKind == JsonValueKind.Number
    ? quality.GetInt32() == 0 : quality.GetString() == "None";

static ResourceSummary ReadResources(string logs)
{
    var samples = new List<Dictionary<string, double>>();
    int malformedLines = 0;
    foreach (string line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("event", out var name) || name.GetString() != "process.summary") continue;
            var data = json.RootElement.GetProperty("data");
            samples.Add(data.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(p => p.Name, p => p.Value.GetDouble()));
        }
        catch (JsonException) { malformedLines++; }
    }
    var metrics = new Dictionary<string, ResourceMetric>();
    foreach (string name in samples.SelectMany(s => s.Keys).Distinct())
    {
        var values = samples.Where(s => s.ContainsKey(name)).Select(s => s[name]).ToArray();
        int half = values.Length / 2;
        double? secondHalfGrowthPercent = half > 0 && values[half] != 0
            ? 100 * (values[^1] - values[half]) / values[half] : null;
        metrics[name] = new(values.Length, values[0], values[^1], values.Min(), values.Max(), secondHalfGrowthPercent);
    }
    double? cpuPercentOfOneCore = null;
    var cpu = samples.Where(s => s.ContainsKey("cpu_time_ticks") && s.ContainsKey("elapsed_ticks")).ToArray();
    if (cpu.Length >= 2)
    {
        double elapsed = cpu[^1]["elapsed_ticks"] - cpu[0]["elapsed_ticks"];
        if (elapsed > 0) cpuPercentOfOneCore = 100 * (cpu[^1]["cpu_time_ticks"] - cpu[0]["cpu_time_ticks"]) / elapsed;
    }
    string[] expected = ["working_set_bytes", "cpu_time_ticks", "managed_heap_bytes", "gc_heap_size_bytes", "gc_fragmented_bytes",
        "gc_total_allocated_bytes", "gc_total_pause_ticks", "gc_collection_count_gen0", "gc_collection_count_gen1", "gc_collection_count_gen2",
        "thread_pool_pending_work_items", "thread_pool_thread_count", "process_handle_count", "http_open_connections_current",
        "http_open_connections_peak", "http_active_requests_current", "http_active_requests_peak", "http_queue_count",
        "http_queue_total_seconds", "http_queue_max_seconds"];
    return new(samples.Count, malformedLines, cpuPercentOfOneCore, expected.Where(name => !metrics.ContainsKey(name)).ToArray(), metrics, samples);
}

static object Statistics(IEnumerable<double?> source)
{
    double[] values = source.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
    if (values.Length == 0) return new { samples = 0 };
    double mean = values.Average();
    double? standardDeviation = values.Length > 1 ? Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Length - 1)) : null;
    return new { samples = values.Length, minimum = values.Min(), maximum = values.Max(), mean, standardDeviation,
        coefficientOfVariationPercent = mean != 0 ? 100 * standardDeviation / Math.Abs(mean) : null };
}

static Task WriteJson(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

sealed record Scenario(string Name, int Rps, int DurationSeconds, int Repetition, bool Warmup);
sealed record ResourceMetric(int Samples, double First, double Last, double Minimum, double Maximum, double? SecondHalfGrowthPercent);
sealed record ResourceSummary(int Samples, int MalformedLines, double? CpuPercentOfOneCore, string[] MissingMetrics,
    Dictionary<string, ResourceMetric> Metrics, List<Dictionary<string, double>> Series);
sealed class RunSummary(Scenario scenario, int exitCode, bool timedOut, long received, long connections, ResourceSummary resources)
{
    public Scenario Scenario { get; } = scenario;
    public int ExitCode { get; } = exitCode;
    public bool TimedOut { get; } = timedOut;
    public long Received { get; } = received;
    public long Connections { get; } = connections;
    public bool FunctionalPassed { get; set; }
    public bool NonSaturatedGatePassed { get; set; }
    public long Planned { get; set; }
    public long Started { get; set; }
    public long Drops { get; set; }
    public double? AchievedRps { get; set; }
    public double? P95Nanoseconds { get; set; }
    public Dictionary<string, bool> Invariants { get; set; } = new();
    public string? SanitizedPlanSha256 { get; set; }
    public JsonElement? Environment { get; set; }
    public JsonElement? Result { get; set; }
    public ResourceSummary Resources { get; } = resources;
    public string? Error { get; set; }
}

sealed class Options
{
    public string Repository { get; private set; } = ".";
    public string? Output { get; private set; }
    public bool Calibrate { get; private set; }
    public bool Quick { get; private set; }
    public bool RequireGates { get; private set; }
    public bool Help { get; private set; }
    public string CaptureMode { get; private set; } = "Off";
    private int[]? durations;
    private int? repetitions;
    private int[] rates = [10, 100];

    public static Options Parse(string[] arguments)
    {
        var options = new Options();
        bool repositorySeen = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            string Value() => ++i < arguments.Length ? arguments[i] : throw new ArgumentException("Missing option value.");
            switch (arguments[i])
            {
                case "--help": case "-h": options.Help = true; break;
                case "--calibrate": options.Calibrate = true; break;
                case "--quick": options.Quick = true; break;
                case "--require-gates": options.RequireGates = true; break;
                case "--output": options.Output = Value(); break;
                case "--capture-mode": options.CaptureMode = Value(); break;
                case "--durations": options.durations = PositiveList(Value(), 600); break;
                case "--rps": options.rates = PositiveList(Value(), 1000); break;
                case "--repetitions": options.repetitions = PositiveList(Value(), 100).Single(); break;
                default:
                    if (arguments[i].StartsWith('-') || repositorySeen) throw new ArgumentException($"Unknown argument: {arguments[i]}");
                    options.Repository = arguments[i]; repositorySeen = true; break;
            }
        }
        if (options.Quick && options.Calibrate) throw new ArgumentException("Choose --quick or --calibrate.");
        if (options.CaptureMode is not ("Off" or "Sampled" or "Complete")) throw new ArgumentException("Invalid capture mode.");
        return options;
    }

    private static int[] PositiveList(string value, int maximum)
    {
        var values = value.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length == 0 || values.Any(v => v < 1 || v > maximum) || values.Distinct().Count() != values.Length)
            throw new ArgumentException($"Values must be distinct integers from 1 through {maximum}.");
        return values;
    }

    public IEnumerable<Scenario> Scenarios()
    {
        if (Calibrate || Quick)
            yield return new("warmup-100rps", 100, Quick ? 2 : 10, 0, true);
        foreach (int duration in durations ?? (Calibrate ? [30, 120, 600] : Quick ? [5] : [30]))
            foreach (int rate in rates)
                for (int repetition = 1; repetition <= (repetitions ?? (Quick ? 2 : Calibrate && duration == 30 ? 3 : 1)); repetition++)
                    yield return new($"fixture-202-{rate}rps-{duration}s-rep{repetition}", rate, duration, repetition, false);
    }
}
