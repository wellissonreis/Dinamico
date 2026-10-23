using System.Diagnostics.Metrics;
using System.Text.Json;
using Dinamico.LoadEngine.Local;

namespace Dinamico.LoadEngine.Tests;

// Observe process-wide subscriptions in the same process, including repeated CLI invocations.
[Collection("HTTP runtime metrics")]
public sealed class LocalCliLifetimeTests
{
    [Fact]
    public async Task Report_creation_failure_releases_metric_subscriptions_before_returning()
    {
        using var meter = new Meter("System.Net.Http");
        var connections = meter.CreateUpDownCounter<long>("http.client.open_connections");
        Assert.False(connections.Enabled);
        string directory = Path.Combine(Path.GetTempPath(), "dinamico-cli-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string config = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, url = "http://127.0.0.1:1/", requestsPerSecond = 1, durationSeconds = 1
            }));
            for (int iteration = 0; iteration < 2; iteration++)
            {
                string report = Path.Combine(directory, "missing", "report.json");
                int code = await LocalCli.RunAsync(["run", "--config", config, "--output", report])
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, code);
                Assert.False(File.Exists(report));
                Assert.False(connections.Enabled);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
