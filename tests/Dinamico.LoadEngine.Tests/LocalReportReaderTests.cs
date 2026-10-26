using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dinamico.LoadEngine.Local;
using Dinamico.LoadEngine.Metrics;
using Dinamico.LoadEngine.Tests.Fixtures;

namespace Dinamico.LoadEngine.Tests;

[Collection("CLI subprocesses")]
public sealed class LocalReportReaderTests
{
    [Fact]
    public async Task Real_report_roundtrips_losslessly_rejects_tampering_and_projects_only_safe_fields()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dinamico-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string canary = "SYNTHETIC_M2_SECRET_13b8";
            await using var fixture = await ControlledHttpFixture.StartAsync(new() { StatusCode = 429, RetryAfterValues = ["3"] });
            string config = Path.Combine(directory, "config.json"), report = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, url = fixture.Address + canary, requestsPerSecond = 10,
                durationSeconds = 1, headers = new[] { new { name = "Authorization", value = canary } }
            }));
            Assert.Equal(0, await LocalCli.RunAsync(["run", "--config", config, "--output", report]));
            var json = JsonNode.Parse(await File.ReadAllTextAsync(report))!.AsObject();
            Guid project = Guid.NewGuid();
            var imported = await Read(json, project);
            Assert.True(imported.DataComplete);
            Assert.Equal(project, imported.ProjectId);
            Assert.Equal(10UL, imported.PlannedTotal);
            var totals = imported.Windows.Aggregate(MetricWindowTotals.Empty, (sum, window) => sum.Append(window));
            Assert.Equal((ulong)fixture.ReceivedCount, totals[MetricCounter.Started]);
            Assert.Equal(totals.FinalStatuses, totals.StatusReceived[329]);
            Assert.NotNull(imported.Diagnostics);
            Assert.Equal((long)totals.TerminalDuration.Count, imported.Diagnostics.Observed);
            Assert.NotEmpty(imported.Diagnostics.Resources);
            var legacy = json.DeepClone();
            legacy.AsObject().Remove("diagnostics");
            Assert.Null((await Read(legacy, project)).Diagnostics);
            var missingResourceField = json.DeepClone();
            missingResourceField["diagnostics"]!["resources"]![0]!.AsObject().Remove("threadPoolPending");
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(missingResourceField, project));
            // Unknown free-text data is never copied into the persisted contract.
            json["untrusted"] = canary;
            json["plan"]!["headers"] = canary;
            var safe = await Read(json, project);
            string metadata = JsonSerializer.Serialize(new { safe.Snapshot, safe.ExecutionId, safe.Completion, safe.Reason });
            Assert.DoesNotContain(canary, metadata);
            Assert.Equal(imported.Snapshot, safe.Snapshot);

            var changed = json.DeepClone();
            changed["result"]!["totals"]!["counters"]!["Started"] = 99;
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(changed, project));
            changed = json.DeepClone();
            changed["windows"]![0]!["encodedWindow"] = Convert.ToBase64String(new byte[MetricWindowCodec.EncodedSize]);
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(changed, project));
            changed = json.DeepClone();
            changed["result"]!["coverage"]!["manifest"]!["lastWindowIndex"] = 50;
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(changed, project));
            changed = json.DeepClone();
            changed["windows"]![0]!.AsObject().Remove("encodedWindow");
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(changed, project));
            string duplicate = json.ToJsonString().Insert(1, "\"schemaVersion\":1,");
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalReportReader.ReadAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(duplicate)), project));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Import_bounds_input_before_parsing_and_invalid_arguments_do_not_open_database()
    {
        using var oversized = new MemoryStream(new byte[LocalReportReader.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalReportReader.ReadAsync(oversized, Guid.NewGuid()));
        Assert.Equal(2, await LocalCli.RunAsync(["storage", "import", "--project", "invalid", "--report", "missing.json"]));
        Assert.Equal(2, await LocalCli.RunAsync(["storage", "list", "--project", Guid.NewGuid().ToString(), "--limit", "0"]));
        Assert.Equal(2, await LocalCli.RunAsync(["storage", "migrate", "--password", "SYNTHETIC_SECRET"]));
    }

    private static Task<Dinamico.Persistence.ExecutionImport> Read(JsonNode json, Guid project) =>
        LocalReportReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString())), project);
}
