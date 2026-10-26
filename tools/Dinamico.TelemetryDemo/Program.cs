using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;
const string sourceName = "Dinamico.TelemetryDemo";
using var source = new ActivitySource(sourceName);
var builder = WebApplication.CreateBuilder(args);
// Keep request URLs, arbitrary headers, baggage and exception text out of telemetry/logs.
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:5095");
var endpoint = Environment.GetEnvironmentVariable("DINAMICO_OTLP_TRACES_ENDPOINT")
    ?? "http://127.0.0.1:4318/v1/traces";
if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var destination)
    || !destination.IsLoopback || destination.Scheme != "http"
    || !string.IsNullOrEmpty(destination.UserInfo) || !string.IsNullOrEmpty(destination.Query)
    || !string.IsNullOrEmpty(destination.Fragment))
    throw new InvalidOperationException("The demo requires a loopback HTTP OTLP endpoint without credentials or query.");

using var provider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("dinamico-telemetry-demo"))
    .AddSource(sourceName)
    .SetSampler(new ParentBasedSampler(new AlwaysOffSampler()))
    .AddOtlpExporter(options =>
    {
        options.Endpoint = destination;
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.TimeoutMilliseconds = 1000;
        options.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
        {
            MaxQueueSize = 512,
            MaxExportBatchSize = 128,
            ScheduledDelayMilliseconds = 500,
            ExporterTimeoutMilliseconds = 1000
        };
    })
    .Build();

var app = builder.Build();
long requestCount = 0;
app.MapGet("/health", () =>
{
    using var process = Process.GetCurrentProcess();
    var managedHeap = GC.GetTotalMemory(false);
    return Results.Ok(new
    {
        status = "ok",
        service = sourceName,
        requestCount = Interlocked.Read(ref requestCount),
        targetWorkingSetBytes = process.WorkingSet64,
        targetManagedHeapBytes = managedHeap >= 0 ? (long?)managedHeap : null
    });
});
app.MapGet("/work", async (HttpContext context, int? delayMs) =>
{
    Interlocked.Increment(ref requestCount);
    var delay = delayMs ?? 150;
    if (delay is < 0 or > 2000)
        return Results.BadRequest(new { error = "delayMs must be between 0 and 2000" });

    // Explicit remote parent avoids exporting ASP.NET's URL/header attributes.
    // Only W3C traceparent is read. No tracestate or baggage is collected.
    ActivityContext.TryParse(context.Request.Headers["traceparent"].ToString(), null,
        isRemote: true, out var parent);
    using var server = source.StartActivity("GET /work", ActivityKind.Server, parent);
    server?.SetTag("http.request.method", "GET");
    server?.SetTag("http.route", "/work");
    using (var dependency = source.StartActivity("controlled-dependency.delay", ActivityKind.Internal))
    {
        dependency?.SetTag("demo.operation", "controlled-delay");
        dependency?.SetTag("demo.delay_ms", delay);
        try
        {
            await Task.Delay(delay, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            dependency?.SetStatus(ActivityStatusCode.Error);
            server?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
    server?.SetTag("http.response.status_code", 200);
    return Results.Ok(new
    {
        delayMs = delay,
        traceId = server?.TraceId.ToHexString(),
        endpointSpanId = server?.SpanId.ToHexString(),
        parentSpanId = server?.ParentSpanId.ToHexString()
    });
});
Console.WriteLine("Telemetry demo: http://127.0.0.1:5095/work?delayMs=150 (health: /health)");
await app.RunAsync();
