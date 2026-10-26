using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dinamico.LoadEngine.Observability;

/// <summary>Optional OTLP/HTTP JSON export of bounded retained requests, after load generation.
/// One run, finite batches, a shared two-second deadline, no retry or unbounded response body.</summary>
public static class DiagnosticTraceExporter
{
    public static async Task<RunDiagnostics> ExportAsync(Guid executionId, DateTimeOffset startedUtc, RunDiagnostics diagnostics,
        string? endpoint = null, string? traceUrlTemplate = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        if (!diagnostics.CorrelationEnabled) return diagnostics with { ExportState = "disabled" };
        endpoint ??= Environment.GetEnvironmentVariable("DINAMICO_OTLP_TRACES_ENDPOINT");
        traceUrlTemplate ??= Environment.GetEnvironmentVariable("DINAMICO_TRACE_URL_TEMPLATE");
        diagnostics = diagnostics with { TraceUrlTemplate = SafeTemplate(traceUrlTemplate) };
        if (string.IsNullOrWhiteSpace(endpoint)) return diagnostics with { ExportState = "not-configured" };
        if (endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var destination)
            || destination.Scheme is not ("http" or "https") || destination.UserInfo.Length != 0
            || destination.Query.Length != 0 || destination.Fragment.Length != 0)
            return diagnostics with { ExportState = "failed" };
        try
        {
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
            using var client = new HttpClient(handler ?? new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ActivityHeadersPropagator = null,
                MaxResponseHeadersLength = 16
            }) { Timeout = Timeout.InfiniteTimeSpan };
            // Authentication is host-only and never retained in diagnostics.
            string? bearer = Environment.GetEnvironmentVariable("DINAMICO_OTLP_BEARER_TOKEN");
            if (!string.IsNullOrEmpty(bearer))
            {
                if (bearer.Length > 4096 || bearer.Any(char.IsControl)) return diagnostics with { ExportState = "failed" };
                client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
            }
            foreach (var batch in diagnostics.Requests.Take(RunDiagnostics.MaximumRequests).Where(r => r.TraceId is not null && r.SpanId is not null).Chunk(128))
            {
                var spans = batch.Select(r => new
                {
                    traceId = r.TraceId, spanId = r.SpanId, name = "load.request", kind = 3,
                    startTimeUnixNano = Nanoseconds(startedUtc, r.StartSeconds),
                    endTimeUnixNano = Nanoseconds(startedUtc, r.StartSeconds + r.DurationMilliseconds / 1000),
                    attributes = new object[]
                    {
                        new { key = "dinamico.execution_id", value = new { stringValue = executionId.ToString() } },
                        new { key = "dinamico.arrival_index", value = new { intValue = r.ArrivalIndex.ToString(CultureInfo.InvariantCulture) } },
                        new { key = "dinamico.outcome", value = new { stringValue = r.Outcome } },
                        new { key = "dinamico.error_kind", value = new { stringValue = r.ErrorKind } },
                        new { key = "http.response.status_code", value = new { intValue = (r.StatusCode ?? 0).ToString(CultureInfo.InvariantCulture) } }
                    },
                    status = new { code = r.Outcome == "HttpCompleted" && r.StatusCode < 400 ? 1 : 2 }
                }).ToArray();
                var payload = new
                {
                    resourceSpans = new[] { new
                    {
                        resource = new { attributes = new[] { new { key = "service.name", value = new { stringValue = "dinamico-generator" } } } },
                        scopeSpans = new[] { new { scope = new { name = "Dinamico.LoadEngine" }, spans } }
                    } }
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, destination) { Content = JsonContent.Create(payload) };
                var sending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                HttpResponseMessage received;
                try { received = await sending.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch
                {
                    _ = DisposeLateResponseAsync(sending);
                    throw;
                }
                using var response = received;
                if (response.StatusCode != HttpStatusCode.OK) return diagnostics with { ExportState = "failed" };
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var body = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    if (body.Length + count > 65536) return diagnostics with { ExportState = "failed" };
                    body.Write(buffer, 0, count);
                }
                if (body.Length != 0)
                {
                    using var json = JsonDocument.Parse(body.ToArray());
                    if (json.RootElement.TryGetProperty("partialSuccess", out var partial)
                        && (partial.TryGetProperty("rejectedSpans", out var rejected)
                            && rejected.ToString() != "0" || partial.TryGetProperty("errorMessage", out var error) && error.GetString()?.Length > 0))
                        return diagnostics with { ExportState = "failed" };
                }
            }
            return diagnostics with { ExportState = "exported" };
        }
        catch (Exception) { return diagnostics with { ExportState = "failed" }; }
    }

    private static async Task DisposeLateResponseAsync(Task<HttpResponseMessage> pending)
    {
        try { using var response = await pending.ConfigureAwait(false); }
        catch (Exception) { /* Late failures must remain observed and private. */ }
    }

    private static string Nanoseconds(DateTimeOffset startedUtc, double seconds) =>
        checked((startedUtc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100 + (long)(seconds * 1_000_000_000)).ToString(CultureInfo.InvariantCulture);

    public static string? SafeTemplate(string? template)
    {
        if (template is null || template.Length > 2048 || template.Any(char.IsControl)) return null;
        const string placeholder = "{traceId}";
        if (!template.Contains(placeholder, StringComparison.Ordinal)
            || template.IndexOf(placeholder, StringComparison.Ordinal) != template.LastIndexOf(placeholder, StringComparison.Ordinal)) return null;
        string replaced = template.Replace(placeholder, new string('a', 32), StringComparison.Ordinal);
        return !replaced.Any(c => c is '{' or '}') && Uri.TryCreate(replaced, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath.Contains(new string('a', 32), StringComparison.Ordinal) ? template : null;
    }
}
