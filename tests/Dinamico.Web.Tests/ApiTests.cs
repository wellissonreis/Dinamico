using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Dinamico.Web.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task GuardsOriginsHostsTargetsAndBodiesWithoutLeakingInput()
    {
        await using var app = await RunningApp.Start("");
        var project = Guid.NewGuid();
        var route = $"/api/projects/{project}/executions";
        var health = await app.Client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("degraded", health.GetProperty("status").GetString());
        Assert.False(health.GetProperty("databaseAvailable").GetBoolean());
        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        foreignHost.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.SendAsync(foreignHost)).StatusCode);
        using var missingClient = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(Plan("http://127.0.0.1:8888")) };
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.SendAsync(missingClient)).StatusCode);
        using var externalOrigin = Request(route, Plan("http://127.0.0.1:8888"));
        externalOrigin.Headers.Add("Origin", "https://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.SendAsync(externalOrigin)).StatusCode);
        foreach (string target in new[] { "http://example.com/SECRET", "http://192.168.1.1/SECRET", "http://127.0.0.1:8000/#SECRET" })
        {
            using var request = Request(route, Plan(target));
            using var response = await app.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync());
        }
        using var local = Request(route, Plan("http://127.0.0.1:8888"));
        local.Headers.Add("Origin", "http://localhost:5173");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.SendAsync(local)).StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json") };
        oversized.Headers.Add("X-Dinamico-Client", "local-ui");
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.SendAsync(oversized)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync($"/api/projects/{Guid.Empty}/active")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(route + "?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.GetAsync(route + "?cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/unknown")).StatusCode);
        Assert.False((await app.Client.GetAsync("/api/health")).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RealExecutionSseCancellationPersistenceAndProjectIsolation()
    {
        string connection = Environment.GetEnvironmentVariable("DINAMICO_TEST_DATABASE")
            ?? throw new InvalidOperationException("DINAMICO_TEST_DATABASE is required; use an exclusive PostgreSQL test database.");
        await using var target = await ControlledHttpFixture.StartAsync(new() { StatusCode = 429, RetryAfterValues = ["2"] });
        await using var app = await RunningApp.Start(connection);
        Assert.True((await app.Client.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("databaseAvailable").GetBoolean());
        Guid project = Guid.NewGuid(), otherProject = Guid.NewGuid();
        string route = $"/api/projects/{project}/executions";
        using var start = Request(route, Plan(target.Address.ToString(), 2));
        using var started = await app.Client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var initial = await started.Content.ReadFromJsonAsync<ExecutionView>();
        Assert.NotNull(initial);
        string item = route + "/" + initial.ExecutionId;
        using var busy = Request($"/api/projects/{otherProject}/executions", Plan(target.Address.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client.SendAsync(busy)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync($"/api/projects/{otherProject}/executions/{initial.ExecutionId}")).StatusCode);
        var other = await app.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{otherProject}/active");
        Assert.Equal(JsonValueKind.Null, other.GetProperty("execution").ValueKind);
        using var streamResponse = await app.Client.GetAsync(item + "/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync());
        Assert.Equal("event: snapshot", await reader.ReadLineAsync());
        string first = (await reader.ReadLineAsync())!;
        Assert.StartsWith("data: ", first);
        Assert.DoesNotContain(target.Address.ToString(), first);
        var done = await Terminal(app.Client, item);
        Assert.Equal("Completed", done.State);
        Assert.Equal("saved", done.Persistence);
        Assert.True(done.DataComplete);
        Assert.True(done.Metrics.HttpCompleted > 0);
        Assert.Equal(done.Metrics.HttpCompleted, done.Metrics.Errors);
        Assert.Equal(0UL, done.Metrics.HttpSuccess);
        Assert.Equal(100, done.Metrics.RateLimitedPercent);
        Assert.Equal(done.Metrics.Planned, done.Metrics.Scheduled);
        Assert.NotEmpty(done.Windows);
        string streamed = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"persistence\":\"saved\"", streamed);
        var history = await app.Client.GetFromJsonAsync<JsonElement>(route + "?limit=1");
        Assert.Equal(initial.ExecutionId, history.GetProperty("items")[0].GetProperty("executionId").GetGuid());
        using var cancelStart = Request(route, Plan(target.Address.ToString(), 30));
        var cancellable = await (await app.Client.SendAsync(cancelStart)).Content.ReadFromJsonAsync<ExecutionView>();
        string cancelItem = route + "/" + cancellable!.ExecutionId;
        using (var disconnect = await app.Client.GetAsync(cancelItem + "/events", HttpCompletionOption.ResponseHeadersRead))
            Assert.Equal(HttpStatusCode.OK, disconnect.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(5.2));
        var live = await app.Client.GetFromJsonAsync<ExecutionView>(cancelItem);
        Assert.Equal("Running", live!.State);
        Assert.True(live.ElapsedSeconds >= 5);
        Assert.True(live.Metrics.Started > 0);
        Assert.NotEmpty(live.Windows);
        using var cancel = Request(route + "/" + cancellable!.ExecutionId + "/cancel", new { });
        Assert.Equal(HttpStatusCode.Accepted, (await app.Client.SendAsync(cancel)).StatusCode);
        Assert.Equal("Cancelled", (await Terminal(app.Client, route + "/" + cancellable.ExecutionId)).State);
        var firstPage = await app.Client.GetFromJsonAsync<JsonElement>(route + "?limit=1");
        Assert.NotEqual(JsonValueKind.Null, firstPage.GetProperty("nextCursor").ValueKind);
        var secondPage = await app.Client.GetFromJsonAsync<JsonElement>(route + "?limit=1&cursor=" + Uri.EscapeDataString(firstPage.GetProperty("nextCursor").GetString()!));
        Assert.Equal(initial.ExecutionId, secondPage.GetProperty("items")[0].GetProperty("executionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
        await using var restarted = await RunningApp.Start(connection);
        var stored = await restarted.Client.GetFromJsonAsync<ExecutionView>(item);
        Assert.Equal(done.Metrics, stored!.Metrics);
        Assert.Equal(done.Windows.Length, stored.Windows.Length);
        Assert.Equal("saved", stored.Persistence);
        using var terminalStream = await restarted.Client.GetAsync(item + "/events");
        Assert.Equal(1, (await terminalStream.Content.ReadAsStringAsync()).Split("event: snapshot").Length - 1);
        using var shutdownStart = Request(route, Plan(target.Address.ToString(), 30));
        var shutdown = await (await restarted.Client.SendAsync(shutdownStart)).Content.ReadFromJsonAsync<ExecutionView>();
        await restarted.Stop();
        var shutdownResult = await app.Client.GetFromJsonAsync<ExecutionView>(route + "/" + shutdown!.ExecutionId);
        Assert.Equal("Cancelled", shutdownResult!.State);
        Assert.Equal("saved", shutdownResult.Persistence);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PersistenceFailureRemainsVisibleWithoutDatabaseExceptionText()
    {
        string connection = Environment.GetEnvironmentVariable("DINAMICO_TEST_DATABASE")
            ?? throw new InvalidOperationException("DINAMICO_TEST_DATABASE requires an exclusive PostgreSQL test database.");
        await using var target = await ControlledHttpFixture.StartAsync();
        await using var app = await RunningApp.Start(connection);
        Guid project = Guid.NewGuid();
        string trigger = "web_fail_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION dinamico_m2.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN IF NEW.project_id = '{project}'::uuid THEN RAISE EXCEPTION 'DO-NOT-RETURN-DB-SECRET'; END IF; RETURN NEW; END $body$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON dinamico_m2.executions FOR EACH ROW EXECUTE FUNCTION dinamico_m2.{trigger}();
            """, db)) await install.ExecuteNonQueryAsync();
        try
        {
            string route = $"/api/projects/{project}/executions";
            using var request = Request(route, Plan(target.Address.ToString()));
            using var response = await app.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var initial = await response.Content.ReadFromJsonAsync<ExecutionView>();
            string item = route + "/" + initial!.ExecutionId;
            var terminal = await Terminal(app.Client, item);
            Assert.Equal("Completed", terminal.State);
            Assert.Equal("failed", terminal.Persistence);
            Assert.DoesNotContain("DO-NOT-RETURN", await app.Client.GetStringAsync(item));
            Assert.DoesNotContain(target.Address.ToString(), await app.Client.GetStringAsync(item));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP TRIGGER {trigger} ON dinamico_m2.executions; DROP FUNCTION dinamico_m2.{trigger}()", db);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<ExecutionView> Terminal(HttpClient client, string item)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var view = await client.GetFromJsonAsync<ExecutionView>(item, deadline.Token);
            if (view!.State != "Running" && view.Persistence != "pending") return view;
            await Task.Delay(100, deadline.Token);
        }
    }
    private static object Plan(string target, int duration = 1) => new { url = target, method = "GET", requestsPerSecond = 10,
        durationSeconds = duration, maxConcurrency = 4, requestTimeoutSeconds = 2, rateLimitMode = "Normal",
        headers = new[] { new { name = "X-Synthetic-Secret", value = "DO-NOT-RETURN" } }, payload = "", testCredentialHeaderIndex = (int?)null, testAuthorizationDeclared = false };
    private static HttpRequestMessage Request(string route, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Dinamico-Client", "local-ui");
        return request;
    }
    private sealed class RunningApp(WebApplication application, HttpClient client) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal Task Stop() => application.StopAsync();
        internal static async Task<RunningApp> Start(string connection)
        {
            var app = LocalWebApplication.Build(port: 0, connectionString: connection);
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(30) });
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await application.StopAsync(); await application.DisposeAsync(); }
    }
}
