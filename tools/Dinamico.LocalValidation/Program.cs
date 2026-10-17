using System.Diagnostics;
using System.Text.Json;
using Dinamico.LoadEngine.Tests.Fixtures;
var root = new DirectoryInfo(Path.GetFullPath(args.Length > 0 ? args[0] : "."));
while (root is not null && !File.Exists(Path.Combine(root.FullName, "Dinamico.slnx"))) root = root.Parent;
var repo = root?.FullName ?? throw new ArgumentException("Run inside the repository or pass its path.");
var evidence = Path.Combine(repo, "TestResults/Ficha012/Smoke");
Directory.CreateDirectory(evidence);
await using var fixture = await ControlledHttpFixture.StartAsync(new() { StatusCode = 202 });
foreach (int rps in new[] { 10, 100 })
{
    string name = $"fixture-202-{rps}rps-30s";
    string config = Path.Combine(evidence, name + ".config.json");
    string report = Path.Combine(evidence, name + ".report.json");
    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { schemaVersion = 2,
        url = fixture.Address.ToString(), requestsPerSecond = rps, durationSeconds = 30,
        maxConcurrency = 32, requestTimeout = "00:00:02" }));
    long before = fixture.ReceivedCount;
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { repo + "/src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll", "run", "--config", config, "--output", report, "--overwrite" }) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var logs = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch { process.Kill(entireProcessTree: true); throw; }
    _ = await stdout;
    await File.WriteAllTextAsync(Path.Combine(evidence, name + ".jsonl"), await logs);
    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
    var result = json.RootElement.GetProperty("result");
    var counters = result.GetProperty("totals").GetProperty("counters");
    long started = counters.GetProperty("Started").GetInt64();
    long planned = result.GetProperty("plannedTotal").GetInt64();
    long drops = counters.GetProperty("SchedulerLate").GetInt64() + counters.GetProperty("ConcurrencyLimit").GetInt64()
        + counters.GetProperty("Policy").GetInt64();
    bool functionalPassed = process.ExitCode == 0 && result.GetProperty("dataComplete").GetBoolean()
        && fixture.ReceivedCount - before == started && counters.GetProperty("HttpCompleted").GetInt64() == started
        && planned == started + drops;
    bool nonSaturatedGatePassed = functionalPassed && drops == 0 && Math.Abs(started - planned) <= planned * 0.02;
    if (!functionalPassed) Environment.ExitCode = 1;
    var summary = new { scenario = name, functionalPassed, nonSaturatedGatePassed,
        exitCode = process.ExitCode, received = fixture.ReceivedCount - before,
        sanitizedPlanSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json.RootElement.GetProperty("plan").GetRawText()))),
        environment = json.RootElement.GetProperty("environment").Clone(), result = result.Clone() };
    await File.WriteAllTextAsync(Path.Combine(evidence, name + ".summary.json"), JsonSerializer.Serialize(summary));
    Console.WriteLine(JsonSerializer.Serialize(new { scenario = name, functionalPassed, nonSaturatedGatePassed, exitCode = process.ExitCode,
        received = fixture.ReceivedCount - before, planned = result.GetProperty("plannedTotal"),
        counters = result.GetProperty("totals").GetProperty("counters"), quality = result.GetProperty("quality"),
        dataComplete = result.GetProperty("dataComplete") }));
}
