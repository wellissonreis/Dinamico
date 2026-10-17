using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dinamico.LoadEngine.Tests.Fixtures;

namespace Dinamico.LoadEngine.Tests;

// Exercise the actual executable and OS pipes; never invoke Program in the test host.
[Collection("CLI subprocesses")]
public sealed class CliProcessTests
{
    [Theory]
    [InlineData(202, "Normal")]
    [InlineData(429, "Authorized")]
    [InlineData(503, "Diagnostic")]
    [InlineData(500, "Normal")]
    public async Task RealFixture_ReconcilesPopulations_AndDoesNotExposeCanaries(int status, string mode)
    {
        const string secret = "SYNTHETIC_CANARY_8dd580";
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            StatusCode = status, RetryAfterValues = [secret]
        });
        using var files = new RunFiles();
        await files.Configure(new
        {
            schemaVersion = 2, url = fixture.Address + "canary/" + secret + "?key=" + secret,
            method = "POST", requestsPerSecond = 10, durationSeconds = 1,
            maxConcurrency = 8, requestTimeout = "00:00:02", rateLimitMode = mode,
            headers = new[] { new { name = "Authorization", value = "Bearer " + secret },
                new { name = "X-Api-Key", value = secret }, new { name = "X-Private-Canary", value = secret } },
            payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)),
            testCredentialHeaderIndex = 0, testAuthorizationDeclared = true
        });
        var run = await Run(files);
        Assert.Equal(0, run.Code);
        Assert.DoesNotContain(secret, run.Stdout + run.Stderr + await File.ReadAllTextAsync(files.Output));
        Assert.Equal("", run.Stdout);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        var result = json.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("dataComplete").GetBoolean());
        var totals = result.GetProperty("totals");
        var counters = totals.GetProperty("counters");
        long started = counters.GetProperty("Started").GetInt64();
        Assert.True(started > 0);
        Assert.Equal(fixture.ReceivedCount, started);
        Assert.Equal(started, counters.GetProperty("HttpCompleted").GetInt64());
        Assert.Equal(started, totals.GetProperty("statuses").GetProperty(status.ToString()).GetInt64());
        Assert.Equal(status == 202 ? started : 0, counters.GetProperty("HttpSuccess").GetInt64());
        Assert.Equal(started, totals.GetProperty("httpCompletedDuration").GetProperty("count").GetInt64());
        Assert.Equal(status == 429 ? 100 : 0, totals.GetProperty("rateLimitedPercent").GetDouble());
        Assert.Equal(status == 202 ? 0 : 100,
            totals.GetProperty("rates").GetProperty("unsuccessfulTerminalPercent").GetDouble());
        long due = result.GetProperty("scheduledDue").GetInt64();
        Assert.Equal(due, started + counters.GetProperty("SchedulerLate").GetInt64() +
            counters.GetProperty("ConcurrencyLimit").GetInt64() + counters.GetProperty("Policy").GetInt64());
        Assert.Equal(result.GetProperty("plannedTotal").GetInt64(), due + result.GetProperty("notScheduledAfterStop").GetInt64());
        await PreserveEvidence($"{status}-{mode}", await File.ReadAllTextAsync(files.Output), run.Stderr);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(200)]
    public async Task BodyLimitPreservesHeadersWithoutClaimingHttpCompletion(int status)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        { StatusCode = status, BodyBytes = 4096, OmitContentLength = true, RetryAfterValues = ["999999999"] });
        using var files = new RunFiles();
        await files.Configure(new { schemaVersion = 2, url = fixture.Address.ToString(), requestsPerSecond = 10,
            durationSeconds = 2, maxResponseBytes = 64 });
        var run = await Run(files);
        Assert.Equal(0, run.Code); // Protocol completion is independent of target errors.
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        var totals = json.RootElement.GetProperty("result").GetProperty("totals");
        var counters = totals.GetProperty("counters");
        long started = counters.GetProperty("Started").GetInt64();
        Assert.True(started > 0);
        Assert.Equal(started, totals.GetProperty("statuses").GetProperty(status.ToString()).GetInt64());
        Assert.Equal(started, counters.GetProperty("ResponseLimitExceeded").GetInt64());
        Assert.Equal(0, counters.GetProperty("HttpCompleted").GetInt64());
        Assert.Equal(0, counters.GetProperty("HttpSuccess").GetInt64());
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("httpCompletedDuration").GetProperty("p95").ValueKind);
        if (status == 429)
        {
            Assert.Equal(started, totals.GetProperty("retryAfter").GetProperty("clamped").GetInt64());
            Assert.Equal(86400, totals.GetProperty("retryAfter").GetProperty("maximumDelaySeconds").GetDouble());
        }
    }

    [Fact]
    public async Task SlowTargetMakesConcurrencyDropsVisible_AndTerminalsReconcile()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new() { HeaderDelay = TimeSpan.FromMilliseconds(200) });
        using var files = new RunFiles();
        await files.Configure(new { schemaVersion = 2, url = fixture.Address.ToString(), requestsPerSecond = 100,
            durationSeconds = 1, maxConcurrency = 1 });
        Assert.Equal(0, (await Run(files)).Code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        var result = json.RootElement.GetProperty("result");
        Assert.Contains("GeneratorLimited", result.GetProperty("quality").GetString());
        var counters = result.GetProperty("totals").GetProperty("counters");
        Assert.True(counters.GetProperty("ConcurrencyLimit").GetInt64() > 0);
        Assert.Equal(fixture.ReceivedCount, counters.GetProperty("Started").GetInt64());
        Assert.Equal(counters.GetProperty("Started").GetInt64(), counters.GetProperty("HttpCompleted").GetInt64());
    }

    [Fact]
    public async Task ExistingReportIsPreserved_AndExplicitOverwritePublishesValidJson()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var files = new RunFiles();
        await files.Configure(Basic(fixture.Address));
        await File.WriteAllTextAsync(files.Output, "keep");
        Assert.Equal(2, (await Run(files)).Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(files.Output));
        Assert.Equal(0, fixture.ReceivedCount);
        Assert.Equal(0, (await Run(files, "--overwrite")).Code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":999,\"url\":\"http://127.0.0.1/SECRET\"}")]
    [InlineData("{\"schemaVersion\":2,\"url\":\"http://example.com/SECRET\"}")]
    [InlineData("{\"schemaVersion\":2,\"url\":\"http://127.0.0.1/\",\"unknownSECRET\":1}")]
    [InlineData("malformed SECRET")]
    public async Task RejectedConfigurationDoesNotLeakValues(string configuration)
    {
        using var files = new RunFiles();
        await File.WriteAllTextAsync(files.Config, configuration);
        var run = await Run(files);
        Assert.Equal(2, run.Code);
        Assert.DoesNotContain("SECRET", run.Stdout + run.Stderr);
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public async Task MissingOutputDirectoryFailsBeforeSendingTraffic()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var files = new RunFiles();
        await files.Configure(Basic(fixture.Address));
        files.Output = Path.Combine(files.Directory, "absent", "report.json");
        Assert.Equal(3, (await Run(files)).Code);
        Assert.Equal(0, fixture.ReceivedCount);
    }

    [Theory]
    [InlineData(2)] // SIGINT / Ctrl+C
    [InlineData(15)] // SIGTERM
    public async Task SignalStopsGenerationAndPublishesReconciledCancelledReport(int signal)
    {
        if (OperatingSystem.IsWindows()) return; // POSIX signals are tested on the Linux runtime.
        await using var fixture = await ControlledHttpFixture.StartAsync(new() { HeaderDelay = TimeSpan.FromSeconds(5) });
        using var files = new RunFiles();
        await files.Configure(new { schemaVersion = 2, url = fixture.Address.ToString(), requestsPerSecond = 10,
            durationSeconds = 30, requestTimeout = "00:00:10", maxConcurrency = 4 });
        using var process = Start(files);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (fixture.ReceivedCount == 0) await Task.Delay(20, deadline.Token);
        Assert.Equal(0, Kill(process.Id, signal));
        await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(130, process.ExitCode);
        Assert.Equal("", await stdout);
        _ = await stderr;
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("Cancelled", result.GetProperty("completion").GetString());
        Assert.True(result.GetProperty("dataComplete").GetBoolean());
        Assert.True(result.GetProperty("notScheduledAfterStop").GetInt64() > 0);
        var totals = result.GetProperty("totals");
        Assert.Equal(0, totals.GetProperty("lastInFlight").GetInt64());
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("httpCompletedDuration").GetProperty("p99").ValueKind);
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("rateLimitedPercent").ValueKind);
        var counters = totals.GetProperty("counters");
        Assert.Equal(counters.GetProperty("Started").GetInt64(), counters.GetProperty("CancelledInFlight").GetInt64());
    }

    [Fact]
    public async Task StdoutIsJson_AndStderrContainsOnlyStructuredLogs()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var files = new RunFiles();
        await files.Configure(Basic(fixture.Address));
        files.Output = "-";
        var run = await Run(files);
        Assert.Equal(0, run.Code);
        using var json = JsonDocument.Parse(run.Stdout);
        Assert.True(json.RootElement.GetProperty("result").GetProperty("dataComplete").GetBoolean());
        foreach (string line in run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var log = JsonDocument.Parse(line);
            Assert.True(log.RootElement.TryGetProperty("event", out _));
        }
    }

    [Fact]
    public async Task BrokenOutputPipeReturnsFailureWithoutHanging()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var files = new RunFiles();
        await files.Configure(Basic(fixture.Address));
        files.Output = "-";
        using var process = Start(files);
        process.StandardOutput.Dispose();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(3, process.ExitCode);
        _ = await stderr;
    }

    [Fact]
    public async Task UnreadStdoutCannotKeepProcessAliveIndefinitely()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var files = new RunFiles();
        await files.Configure(Basic(fixture.Address));
        files.Output = "-";
        using var process = Start(files);
        var pipe = (System.IO.Pipes.PipeStream)process.StandardOutput.BaseStream;
        Assert.True(SetPipeSize(pipe.SafePipeHandle.DangerousGetHandle().ToInt32(), 1031, 4096) >= 4096);
        var stderr = process.StandardError.ReadToEndAsync();
        // Do not read stdout. The pipe fills with windows/report, exercising real OS backpressure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(3, process.ExitCode);
        _ = await stderr;
    }

    [Fact]
    public async Task TimeoutAfter429HeadersPreservesSignalAndTerminalDenominator()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        { StatusCode = 429, BodyBytes = 4096, ChunkDelay = TimeSpan.FromMilliseconds(500), RetryAfterValues = ["2", "3"] });
        using var files = new RunFiles();
        await files.Configure(new { schemaVersion = 2, url = fixture.Address.ToString(), requestsPerSecond = 10,
            durationSeconds = 2, requestTimeout = "00:00:00.200" });
        var run = await Run(files);
        Assert.Equal(0, run.Code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files.Output));
        var totals = json.RootElement.GetProperty("result").GetProperty("totals");
        var counters = totals.GetProperty("counters");
        long started = counters.GetProperty("Started").GetInt64();
        Assert.True(started > 0);
        Assert.Equal(started, totals.GetProperty("statuses").GetProperty("429").GetInt64());
        Assert.Equal(started, counters.GetProperty("RequestTimedOut").GetInt64());
        Assert.Equal(started, totals.GetProperty("retryAfter").GetProperty("kinds").GetProperty("Invalid").GetInt64());
        Assert.Equal(0, counters.GetProperty("HttpCompleted").GetInt64());
        Assert.Equal(started, totals.GetProperty("terminalDuration").GetProperty("count").GetInt64());
        await PreserveEvidence("429-timeout", await File.ReadAllTextAsync(files.Output), run.Stderr);
    }

    [Fact]
    public async Task ManyInvalidHeadersCannotProduceUnboundedErrorLine()
    {
        using var files = new RunFiles();
        await files.Configure(new { schemaVersion = 2, url = "http://127.0.0.1/",
            headers = Enumerable.Range(0, 128).Select(i => new { name = "Invalid canary " + i, value = "synthetic" }) });
        var run = await Run(files);
        Assert.Equal(2, run.Code);
        Assert.DoesNotContain("canary", run.Stderr);
        foreach (string line in run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 4096);
            using var json = JsonDocument.Parse(line);
            Assert.True(json.RootElement.GetProperty("data").GetProperty("errorCount").GetInt32() > 5);
            Assert.Equal(5, json.RootElement.GetProperty("data").GetProperty("errors").GetArrayLength());
        }
    }

    private static async Task PreserveEvidence(string scenario, string report, string logs)
    {
        string? directory = Environment.GetEnvironmentVariable("DINAMICO_EVIDENCE_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, scenario + ".json"), report);
        await File.WriteAllTextAsync(Path.Combine(directory, scenario + ".jsonl"), logs);
    }

    private static object Basic(Uri uri) => new { schemaVersion = 2, url = uri.ToString(), requestsPerSecond = 2, durationSeconds = 1 };
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int SetPipeSize(int descriptor, int command, int capacity);
    private static Process Start(RunFiles files, params string[] extra)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dinamico.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root unavailable.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(directory.FullName, "src", "Dinamico.LoadEngine.Local", "bin", configuration, "net10.0", "Dinamico.LoadEngine.Local.dll"));
        foreach (string arg in new[] { "run", "--config", files.Config, "--output", files.Output }.Concat(extra)) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task<(int Code, string Stdout, string Stderr)> Run(RunFiles files, params string[] extra)
    {
        using var process = Start(files, extra);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
    private sealed class RunFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "dinamico-cli-" + Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Directory, "config.json");
        public string Output { get; set; }
        public RunFiles() { System.IO.Directory.CreateDirectory(Directory); Output = Path.Combine(Directory, "report.json"); }
        public Task Configure(object config) => File.WriteAllTextAsync(Config, JsonSerializer.Serialize(config));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

// Real open-loop scheduling is sensitive to CPU contention from unrelated test classes.
[CollectionDefinition("CLI subprocesses", DisableParallelization = true)]
public sealed class CliProcessCollection { }
