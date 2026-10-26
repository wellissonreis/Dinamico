using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dinamico.LoadEngine.Tests;

public sealed class TelemetryCorrelationIntegrationTests
{
    [Fact]
    public async Task Instrumented_target_links_request_endpoint_and_slow_dependency_to_real_otlp_export()
    {
        string sourceName = "Dinamico.Fixture." + Guid.NewGuid();
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(sourceName);
        var exports = new ConcurrentBag<JsonElement>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/target", async (HttpContext context) =>
        {
            Assert.True(ActivityContext.TryParse(context.Request.Headers["traceparent"], null, out var parent));
            using var endpoint = source.StartActivity("fixture.endpoint", ActivityKind.Server, parent);
            using (var dependency = source.StartActivity("fixture.slow-dependency", ActivityKind.Client))
                await Task.Delay(20, context.RequestAborted);
            await context.Response.WriteAsync("ok");
        });
        app.MapPost("/v1/traces", async (HttpContext context) =>
        {
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            exports.Add(json.RootElement.Clone());
            await context.Response.WriteAsJsonAsync(new { });
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var plan = LocalPlanValidator.Validate(new()
        {
            Url = address + "/target", RequestsPerSecond = 1, DurationSeconds = 1,
            CaptureMode = RequestCaptureMode.Complete, CorrelateRequests = true
        }, Guid.NewGuid()).Plan!;
        using var transport = new LocalHttpTransport(plan);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var result = await transport.SendAsync(0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        var capture = new RunDiagnosticCollector(RequestCaptureMode.Complete, true);
        capture.Record(result, 0, result.HeadersMilliseconds);
        var exported = await DiagnosticTraceExporter.ExportAsync(plan.ExecutionId, started, capture.Snapshot(true),
            address + "/v1/traces", "https://traces.example/trace/{traceId}");
        Assert.Equal("exported", exported.ExportState);
        var payload = Assert.Single(exports);
        var clientSpan = payload.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans")[0];
        Assert.Equal(result.TraceId, clientSpan.GetProperty("traceId").GetString());
        Assert.Equal(result.SpanId, clientSpan.GetProperty("spanId").GetString());
        var server = Assert.Single(stopped, a => a.OperationName == "fixture.endpoint");
        var dependencySpan = Assert.Single(stopped, a => a.OperationName == "fixture.slow-dependency");
        Assert.Equal(result.TraceId, server.TraceId.ToHexString());
        Assert.Equal(result.SpanId, server.ParentSpanId.ToHexString());
        Assert.Equal(server.TraceId, dependencySpan.TraceId);
        Assert.Equal(server.SpanId, dependencySpan.ParentSpanId);
        Assert.True(dependencySpan.Duration > TimeSpan.Zero);
        Assert.DoesNotContain(address, JsonSerializer.Serialize(exported));
    }
}
