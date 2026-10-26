using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Observability;
using Dinamico.LoadEngine.Timing;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class DiagnosticCaptureTests
{
    [Fact]
    public void Off_counts_terminals_without_retaining_individual_requests()
    {
        var collector = new RunDiagnosticCollector(RequestCaptureMode.Off, false);
        for (int i = 0; i < 3000; i++) collector.Record(Completed(i), i, null);
        var result = collector.Snapshot(true);
        Assert.Equal(3000, result.Observed);
        Assert.Equal(0, result.Retained);
        Assert.Equal(3000, result.Discarded);
        Assert.Empty(result.Requests);
        Assert.False(result.CaptureComplete);
        Assert.Equal("disabled", result.ExportState);
    }

    [Fact]
    public void Complete_requires_draining_and_reports_overflow_as_partial()
    {
        var collector = new RunDiagnosticCollector(RequestCaptureMode.Complete, true);
        for (int i = 0; i < 2000; i++) collector.Record(Completed(i), i, .25);
        Assert.False(collector.Snapshot().CaptureComplete);
        var complete = collector.Snapshot(true);
        Assert.True(complete.CaptureComplete);
        Assert.Equal(2000, complete.Retained);
        Assert.Equal(0, complete.Discarded);
        Assert.Equal(2000, complete.Requests.Select(r => r.ArrivalIndex).Distinct().Count());
        collector.Record(Completed(2000), 2000, null);
        var overflow = collector.Snapshot(true);
        Assert.False(overflow.CaptureComplete);
        Assert.Equal(2001, overflow.Observed);
        Assert.Equal(2000, overflow.Retained);
        Assert.Equal(1, overflow.Discarded);
    }

    [Fact]
    public void Sampled_reserves_failures_and_slow_operations_with_bounded_coverage()
    {
        var collector = new RunDiagnosticCollector(RequestCaptureMode.Sampled, false);
        for (int i = 0; i < 3000; i++)
        {
            var observation = i is >= 1000 and < 1250
                ? new RequestObservation(i, RequestOutcome.RequestTimedOut, TimeSpan.FromMilliseconds(2), null, 0, RequestErrorKind.RequestTimeout)
                : Completed(i, i >= 2500 ? 1000 + i : 1);
            collector.Record(observation, i, null);
        }
        var result = collector.Snapshot(true);
        Assert.Equal(3000, result.Observed);
        Assert.Equal(1000, result.Retained);
        Assert.Equal(2000, result.Discarded);
        Assert.False(result.CaptureComplete);
        Assert.Equal(250, result.Requests.Count(r => r.Outcome == "RequestTimedOut"));
        Assert.Equal(500, result.Requests.Count(r => r.ArrivalIndex >= 2500));
        Assert.Equal(1000, result.Requests.Select(r => r.ArrivalIndex).Distinct().Count());
        Assert.Equal(result.Requests.OrderBy(r => r.ArrivalIndex), result.Requests);
    }

    [Fact]
    public void Resource_snapshots_are_bounded_independently_of_capture_mode()
    {
        var collector = new RunDiagnosticCollector(RequestCaptureMode.Off, false);
        for (ulong i = 0; i < 200; i++) collector.Record(new ResourceSample(i, i, null, null, null, null, null, null, null, null, 0, 1, null, null));
        var result = collector.Snapshot();
        Assert.Equal(133, result.Resources.Length);
        Assert.Equal(132UL, result.Resources[^1].WindowIndex);
        Assert.Null(result.Resources[0].WorkingSetBytes);
    }

    [Theory]
    [InlineData(RequestCaptureMode.Complete, false, 2001, null)]
    [InlineData(RequestCaptureMode.Off, true, 1, null)]
    [InlineData(RequestCaptureMode.Sampled, true, 1, "TraceParent")]
    [InlineData(RequestCaptureMode.Sampled, true, 1, "tracestate")]
    public void Invalid_capture_or_correlation_configuration_is_rejected(RequestCaptureMode mode, bool correlate, int total, string? header)
    {
        var result = LocalPlanValidator.Validate(Configuration(mode, correlate) with
        {
            RequestsPerSecond = total,
            Headers = header is null ? [] : [new(header, "canary")]
        }, Guid.NewGuid());
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == (total > 2000 ? ConfigurationField.CaptureMode : ConfigurationField.CorrelateRequests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_exposes_explicit_correlation_and_headers_duration(bool correlate)
    {
        string? parent = null;
        using var source = new ActivitySource("Dinamico.Tests.DiagnosticCapture");
        using var listener = Listen();
        using var ambient = source.StartActivity("ambient");
        Assert.NotNull(ambient);
        var plan = LocalPlanValidator.Validate(Configuration(RequestCaptureMode.Sampled, correlate), Guid.NewGuid()).Plan!;
        using var handler = new InspectHandler(request =>
        {
            parent = request.Headers.TryGetValues("traceparent", out var values) ? Assert.Single(values) : null;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        using var transport = new LocalHttpTransport(plan, StopwatchMonotonicClock.Instance, TimeProvider.System, handler);
        var observation = await transport.SendAsync(7);
        Assert.Equal(RequestOutcome.HttpCompleted, observation.Outcome);
        Assert.NotNull(observation.HeadersMilliseconds);
        Assert.InRange(observation.HeadersMilliseconds.Value, 0, observation.TerminalDuration.TotalMilliseconds);
        if (correlate)
        {
            Assert.Matches("^[0-9a-f]{32}$", observation.TraceId!);
            Assert.Matches("^[0-9a-f]{16}$", observation.SpanId!);
            Assert.Equal($"00-{observation.TraceId}-{observation.SpanId}-01", parent);
            Assert.NotEqual(ambient.TraceId.ToHexString(), observation.TraceId);
            var collector = new RunDiagnosticCollector(RequestCaptureMode.Sampled, true);
            collector.Record(observation, 0, observation.HeadersMilliseconds);
            var sample = Assert.Single(collector.Snapshot().Requests);
            Assert.Equal(observation.TraceId, sample.TraceId);
            Assert.Equal(observation.SpanId, sample.SpanId);
            Assert.Equal(observation.HeadersMilliseconds, sample.HeadersMilliseconds);
        }
        else
        {
            Assert.Null(parent); Assert.Null(observation.TraceId); Assert.Null(observation.SpanId);
        }
    }

    [Fact]
    public async Task Sampled_correlation_limits_propagation_to_one_thousand_planned_intents()
    {
        int propagated = 0;
        var plan = LocalPlanValidator.Validate(Configuration(RequestCaptureMode.Sampled, true) with
            { RequestsPerSecond = 1000, DurationSeconds = 3 }, Guid.NewGuid()).Plan!;
        using var handler = new InspectHandler(request =>
        {
            if (request.Headers.Contains("traceparent")) propagated++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        });
        using var transport = new LocalHttpTransport(plan, StopwatchMonotonicClock.Instance, TimeProvider.System, handler);
        for (int i = 0; i < 3000; i++)
        {
            var result = await transport.SendAsync(i);
            Assert.Equal(i % 3 == 0, result.TraceId is not null);
        }
        Assert.Equal(1000, propagated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_socket_transport_does_not_automatically_propagate_ambient_activity(bool correlate)
    {
        using var listener = Listen();
        using var source = new ActivitySource("Dinamico.Tests.DiagnosticCapture.Socket");
        using var ambient = source.StartActivity("ambient");
        Assert.NotNull(ambient);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = ReceiveRequest();
        var configuration = Configuration(RequestCaptureMode.Sampled, correlate) with
            { Url = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/" };
        using var transport = new LocalHttpTransport(LocalPlanValidator.Validate(configuration, Guid.NewGuid()).Plan!);
        var observation = await transport.SendAsync(0, deadline.Token);
        string request = await receive;
        Assert.Equal(RequestOutcome.HttpCompleted, observation.Outcome);
        if (correlate) Assert.Contains($"traceparent: 00-{observation.TraceId}-{observation.SpanId}-01", request, StringComparison.OrdinalIgnoreCase);
        else Assert.DoesNotContain("traceparent:", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ambient.TraceId.ToHexString(), request, StringComparison.OrdinalIgnoreCase);

        async Task<string> ReceiveRequest()
        {
            using var socket = await server.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var headers = new StringBuilder();
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line) headers.AppendLine(line);
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), deadline.Token);
            return headers.ToString();
        }
    }

    private static RequestObservation Completed(long index, double milliseconds = 1) => new(index,
        RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(milliseconds), 200, 0);
    private static LoadTestConfiguration Configuration(RequestCaptureMode mode, bool correlate) => new()
        { Url = "http://127.0.0.1/", RequestsPerSecond = 1, DurationSeconds = 1, CaptureMode = mode, CorrelateRequests = correlate };
    private static ActivityListener Listen()
    {
        var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
    private sealed class InspectHandler(Func<HttpRequestMessage, HttpResponseMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(inspect(request));
    }
}
