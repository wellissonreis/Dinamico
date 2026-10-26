using System.Net;
using System.Text.Json;
using Dinamico.LoadEngine.Observability;

namespace Dinamico.LoadEngine.Tests;

public sealed class DiagnosticTraceExporterTests
{
    [Fact]
    public async Task Exports_bounded_otlp_spans_with_hex_ids_nanoseconds_and_terminal_status()
    {
        var started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var execution = Guid.NewGuid();
        string? captured = null;
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            captured = await request.Content.ReadAsStringAsync();
            return Response(HttpStatusCode.OK, "{}");
        });
        var input = Diagnostics() with { Requests = [Sample(), Sample() with { ArrivalIndex = 2, StatusCode = 500 }] };
        var result = await DiagnosticTraceExporter.ExportAsync(execution, started, input,
            "http://collector.invalid/v1/traces", "https://traces.example/trace/{traceId}", handler);
        Assert.Equal("exported", result.ExportState);
        Assert.Equal("https://traces.example/trace/{traceId}", result.TraceUrlTemplate);
        Assert.NotNull(captured);
        using var document = JsonDocument.Parse(captured);
        var spans = document.RootElement.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans");
        Assert.Equal(2, spans.GetArrayLength());
        Assert.Equal(new string('a', 32), spans[0].GetProperty("traceId").GetString());
        Assert.Equal(new string('b', 16), spans[0].GetProperty("spanId").GetString());
        Assert.Equal("1700000001250000000", spans[0].GetProperty("startTimeUnixNano").GetString());
        Assert.Equal("1700000001275000000", spans[0].GetProperty("endTimeUnixNano").GetString());
        Assert.Equal(1, spans[0].GetProperty("status").GetProperty("code").GetInt32());
        Assert.Equal(2, spans[1].GetProperty("status").GetProperty("code").GetInt32());
        var attributes = spans[0].GetProperty("attributes").EnumerateArray().ToDictionary(a => a.GetProperty("key").GetString()!);
        Assert.Equal(execution.ToString(), attributes["dinamico.execution_id"].GetProperty("value").GetProperty("stringValue").GetString());
        Assert.Equal("1", attributes["dinamico.arrival_index"].GetProperty("value").GetProperty("intValue").GetString());
        Assert.Equal("200", attributes["http.response.status_code"].GetProperty("value").GetProperty("intValue").GetString());
        Assert.DoesNotContain("http.url", captured);
        Assert.DoesNotContain("header", captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", captured, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("collector.invalid", captured);
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Export_splits_finite_batches_and_skips_operations_without_ids()
    {
        int calls = 0, exported = 0;
        using var handler = new Handler(async (request, _) =>
        {
            calls++;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            int count = document.RootElement.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans").GetArrayLength();
            Assert.InRange(count, 1, 128);
            exported += count;
            return Response(HttpStatusCode.OK, "{}");
        });
        var input = Diagnostics() with { Requests = [.. Enumerable.Range(0, 257).Select(i => Sample() with { ArrivalIndex = i }), Sample() with { TraceId = null, SpanId = null }] };
        var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, input, "http://collector.invalid/v1/traces", handler: handler);
        Assert.Equal("exported", result.ExportState);
        Assert.Equal(3, calls);
        Assert.Equal(257, exported);
    }

    [Theory]
    [InlineData(200, "{\"partialSuccess\":{\"rejectedSpans\":\"1\"}}")]
    [InlineData(200, "{\"partialSuccess\":{\"rejectedSpans\":\"0\",\"errorMessage\":\"secret-canary\"}}")]
    [InlineData(503, "secret-canary")]
    [InlineData(200, "not-json-secret-canary")]
    public async Task Rejected_partial_or_failed_export_is_reported_without_throwing_or_retaining_response(int code, string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)code, body)));
        var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, Diagnostics(), "http://collector.invalid/v1/traces", handler: handler);
        Assert.Equal("failed", result.ExportState);
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
        Assert.Single(result.Requests);
        Assert.True(result.CaptureComplete);
    }

    [Fact]
    public async Task Export_timeout_or_handler_failure_is_isolated_from_canonical_diagnostics()
    {
        foreach (bool hang in new[] { false, true })
        {
            using var handler = new Handler(async (_, cancellationToken) =>
            {
                if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new HttpRequestException("secret-canary");
            });
            var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, Diagnostics(),
                "http://collector.invalid/v1/traces", handler: handler, timeout: TimeSpan.FromMilliseconds(20));
            Assert.Equal("failed", result.ExportState);
            Assert.True(result.CaptureComplete);
            Assert.Equal(1, result.Retained);
            Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
        }
    }

    [Fact]
    public async Task Opt_out_does_not_send_even_when_an_export_endpoint_is_provided()
    {
        int calls = 0;
        using var handler = new Handler((_, _) => { calls++; throw new InvalidOperationException(); });
        var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Diagnostics() with { CorrelationEnabled = false }, "http://collector.invalid/v1/traces", handler: handler);
        Assert.Equal("disabled", result.ExportState);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Handler_ignoring_cancellation_cannot_block_completion()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => release.Task);
        try
        {
            var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, Diagnostics(),
                "http://collector.invalid/v1/traces", handler: handler, timeout: TimeSpan.FromMilliseconds(20))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("failed", result.ExportState);
            Assert.True(result.CaptureComplete);
        }
        finally { release.TrySetResult(Response(HttpStatusCode.OK, "{}")); }
    }

    [Fact]
    public async Task Oversized_collector_response_is_bounded_and_does_not_change_capture()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new string('x', 65537))));
        var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, Diagnostics(),
            "http://collector.invalid/v1/traces", handler: handler);
        Assert.Equal("failed", result.ExportState);
        Assert.True(result.CaptureComplete);
        Assert.Single(result.Requests);
    }

    [Theory]
    [InlineData("javascript:{traceId}")]
    [InlineData("https://user:secret-canary@traces.example/{traceId}")]
    [InlineData("https://traces.example/{traceId}?token=secret-canary")]
    [InlineData("https://traces.example/{traceId}#{traceId}")]
    [InlineData("https://traces.example/no-placeholder")]
    [InlineData("https://{traceId}.example/")]
    [InlineData("https://traces.example/{traceId}\r\nsecret-canary")]
    public async Task Unsafe_trace_templates_are_discarded(string template)
    {
        Assert.Null(DiagnosticTraceExporter.SafeTemplate(template));
        using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var result = await DiagnosticTraceExporter.ExportAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, Diagnostics(),
            "http://collector.invalid/v1/traces", template, handler);
        Assert.Null(result.TraceUrlTemplate);
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
    }

    private static RequestSample Sample() => new(1, 1.25, 25, 5, 200, "HttpCompleted", "None", new string('a', 32), new string('b', 16));
    private static RunDiagnostics Diagnostics() => new(1, "Complete", true, 1, 1, 0, true, [], [Sample()], null);
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
