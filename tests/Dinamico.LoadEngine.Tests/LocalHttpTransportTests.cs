using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Timing;
using Dinamico.LoadEngine.Transport;
using Dinamico.LoadEngine.Tests.Fixtures;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class LocalHttpTransportTests
{
    [Fact]
    public async Task Sends_headers_and_payload_and_reuses_connection()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions { BodyBytes = 32 });
        using var transport = CreateTransport(fixture.Address, "POST", [new HttpHeader("X-Trace", "fixture")], "request-body"u8.ToArray());

        RequestObservation first = await transport.SendAsync(0);
        RequestObservation second = await transport.SendAsync(1);

        Assert.Equal(RequestOutcome.HttpCompleted, first.Outcome);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(32, first.ResponseBodyBytes);
        Assert.Equal(RequestErrorKind.None, first.ErrorKind);
        Assert.Equal(2, fixture.ReceivedCount);
        Assert.Equal(2 * "request-body"u8.Length, fixture.RequestBodyBytesReceived);
        Assert.Equal(1, fixture.ConnectionCount);
    }

    [Fact]
    public async Task Captures_429_retry_after_before_truncated_body_failure()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429,
            RetryAfterValues = ["17"],
            BodyBytes = 100,
            TruncateBody = true
        });
        using var transport = CreateTransport(fixture.Address);

        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RequestObservation> pending = transport.SendAsync(3, default, _ => headers.TrySetResult());
        await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ReleaseTruncation();
        RequestObservation result = await pending;

        Assert.Equal(RequestOutcome.TransportFailed, result.Outcome);
        Assert.Equal(RequestErrorKind.ResponseRead, result.ErrorKind);
        Assert.Equal(429, result.StatusCode);
        Assert.True(result.IsRateLimited);
        Assert.Equal(RetryAfterKind.DeltaSeconds, result.RetryAfter.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter.Delay);
    }

    [Fact]
    public async Task Distinguishes_missing_retry_after_and_not_applicable()
    {
        await using var limited = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions { StatusCode = 429 });
        using var limitedTransport = CreateTransport(limited.Address);
        RequestObservation absent = await limitedTransport.SendAsync(0);
        Assert.Equal(RetryAfterKind.Missing, absent.RetryAfter.Kind);

        await using var unavailable = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 503,
            RetryAfterValues = ["25"]
        });
        using var unavailableTransport = CreateTransport(unavailable.Address);
        RequestObservation status503 = await unavailableTransport.SendAsync(1);
        Assert.Equal(RequestOutcome.HttpCompleted, status503.Outcome);
        Assert.False(status503.IsRateLimited);
        Assert.Equal(RetryAfterKind.NotApplicable, status503.RetryAfter.Kind);
    }

    [Fact]
    public async Task Enforces_response_limit_without_buffering_full_body()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            BodyBytes = 2 * 1024 * 1024,
            ChunkBytes = 16 * 1024,
            OmitContentLength = true
        });
        using var transport = CreateTransport(fixture.Address, maxResponseBytes: 1024);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.ResponseLimitExceeded, result.Outcome);
        Assert.Equal(RequestErrorKind.ResponseLimit, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1025, result.ResponseBodyBytes);
    }

    [Fact]
    public async Task Cancellation_during_body_read_is_explicit_and_preserves_headers()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429,
            RetryAfterValues = ["5"],
            BodyBytes = 1024,
            ChunkBytes = 16,
            ChunkDelay = TimeSpan.FromMilliseconds(20)
        });
        using var transport = CreateTransport(fixture.Address);
        using var stop = new CancellationTokenSource();
        var headers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RequestObservation> pending = transport.SendAsync(0, stop.Token, _ => headers.TrySetResult());
        await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(60);
        stop.Cancel();

        RequestObservation result = await pending;
        Assert.Equal(RequestOutcome.CancelledInFlight, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(RetryAfterKind.DeltaSeconds, result.RetryAfter.Kind);
        Assert.Equal(RequestErrorKind.Cancellation, result.ErrorKind);

        RequestObservation recovered = await transport.SendAsync(1);
        Assert.Equal(RequestOutcome.HttpCompleted, recovered.Outcome);
        Assert.Equal(1024, recovered.ResponseBodyBytes);
        Assert.Equal(2, fixture.ReceivedCount);
        await WaitUntilAsync(() => fixture.ActiveRequests == 0);
    }

    [Fact]
    public async Task Redirect_is_returned_without_following_it()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions { Redirect = true });
        using var transport = CreateTransport(fixture.Address);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(302, result.StatusCode);
        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(1, fixture.ReceivedCount);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    public async Task Complete_2xx_response_satisfies_versioned_success_rule(int statusCode)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions { StatusCode = statusCode });
        using var transport = CreateTransport(fixture.Address);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.True(result.IsAccepted(HttpSuccessRule.Completed2xxV1));
    }

    [Fact]
    public async Task Head_response_completes_without_counting_advertised_entity_length()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new HttpFixtureOptions { BodyBytes = 4096 });
        using var transport = CreateTransport(fixture.Address, method: "HEAD", maxResponseBytes: 1024);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(0, result.ResponseBodyBytes);
    }

    [Theory]
    [InlineData(200, 0, true)]
    [InlineData(204, 4096, true)]
    [InlineData(205, 4096, true)]
    [InlineData(304, 4096, false)]
    [InlineData(500, 16, false)]
    public async Task Complete_empty_and_error_responses_keep_http_semantics(int status, int bodyBytes, bool accepted)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            StatusCode = status, BodyBytes = bodyBytes
        });
        using var transport = CreateTransport(fixture.Address);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(RequestErrorKind.None, result.ErrorKind);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(status == 500 ? 16 : 0, result.ResponseBodyBytes);
        Assert.Equal(accepted, result.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.Equal(1, fixture.ReceivedCount);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1025)]
    public async Task Pool_recovers_after_declared_or_streamed_limit(bool omitContentLength, int bytesRead)
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            StatusCode = 429, AlternateStatusCode = 200, RetryAfterValues = ["9"],
            BodyBytes = 4096, AlternateBodyBytes = 16, OmitContentLength = omitContentLength
        });
        using var transport = CreateTransport(fixture.Address, maxResponseBytes: 1024);

        RequestObservation limited = await transport.SendAsync(0);
        RequestObservation recovered = await transport.SendAsync(1);

        Assert.Equal(RequestOutcome.ResponseLimitExceeded, limited.Outcome);
        Assert.Equal(RequestErrorKind.ResponseLimit, limited.ErrorKind);
        Assert.Equal(429, limited.StatusCode);
        Assert.Equal(bytesRead, limited.ResponseBodyBytes);
        Assert.Equal(TimeSpan.FromSeconds(9), limited.RetryAfter.Delay);
        Assert.Equal(RequestOutcome.HttpCompleted, recovered.Outcome);
        Assert.Equal(200, recovered.StatusCode);
        Assert.Equal(16, recovered.ResponseBodyBytes);
        Assert.Equal(2, fixture.ReceivedCount);
        await WaitUntilAsync(() => fixture.ActiveRequests == 0);
    }

    [Fact]
    public async Task Cancellation_before_headers_preserves_not_observed_and_pool_recovers()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            HeaderDelay = TimeSpan.FromSeconds(1)
        });
        using var transport = CreateTransport(fixture.Address);
        using var stop = new CancellationTokenSource();
        int headersObserved = 0;
        Task<RequestObservation> pending = transport.SendAsync(0, stop.Token, _ => headersObserved++);
        await WaitUntilAsync(() => fixture.ReceivedCount == 1);
        stop.Cancel();

        RequestObservation cancelled = await pending;
        RequestObservation recovered = await transport.SendAsync(1);

        Assert.Equal(RequestOutcome.CancelledInFlight, cancelled.Outcome);
        Assert.Equal(RequestErrorKind.Cancellation, cancelled.ErrorKind);
        Assert.Null(cancelled.StatusCode);
        Assert.Equal(0, cancelled.ResponseBodyBytes);
        Assert.Equal(RetryAfterKind.NotObserved, cancelled.RetryAfter.Kind);
        Assert.Equal(0, headersObserved);
        Assert.Equal(RequestOutcome.HttpCompleted, recovered.Outcome);
        Assert.Equal(2, fixture.ReceivedCount);
        await WaitUntilAsync(() => fixture.ActiveRequests == 0);
    }

    [Fact]
    public async Task Precancelled_token_does_not_connect_or_observe_headers()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        using var transport = CreateTransport(fixture.Address);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        RequestObservation result = await transport.SendAsync(0, stop.Token,
            _ => Assert.Fail("A precancelled request cannot observe headers."));

        Assert.Equal(RequestOutcome.CancelledInFlight, result.Outcome);
        Assert.Equal(RequestErrorKind.Cancellation, result.ErrorKind);
        Assert.Null(result.StatusCode);
        Assert.Equal(RetryAfterKind.NotObserved, result.RetryAfter.Kind);
        Assert.Equal(0, fixture.ReceivedCount);
        Assert.Equal(0, fixture.ConnectionCount);
    }

    [Fact]
    public async Task Refused_loopback_connection_is_a_connect_failure_without_headers()
    {
        // Reserve the port without listening so another process cannot claim it before connect.
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        using var transport = CreateTransport(new Uri($"http://127.0.0.1:{port}/"));

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.TransportFailed, result.Outcome);
        Assert.Equal(RequestErrorKind.Connect, result.ErrorKind);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, result.ResponseBodyBytes);
        Assert.Equal(RetryAfterKind.NotObserved, result.RetryAfter.Kind);
    }

    [Theory]
    [InlineData("Wed, 21 Oct 2026 12:00:17 GMT", RetryAfterKind.HttpDate, 17, false)]
    [InlineData("Wed, 21 Oct 2026 11:59:59 GMT", RetryAfterKind.HttpDate, 0, false)]
    [InlineData("Fri, 23 Oct 2026 12:00:00 GMT", RetryAfterKind.HttpDate, 86400, true)]
    [InlineData("999999999", RetryAfterKind.DeltaSeconds, 86400, true)]
    [InlineData("invalid", RetryAfterKind.Invalid, null, false)]
    [InlineData("5, 7", RetryAfterKind.Invalid, null, false)]
    public async Task Retry_after_is_normalized_from_real_headers(string value, RetryAfterKind kind,
        int? seconds, bool clamped)
    {
        var reference = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            StatusCode = 429, RetryAfterValues = [value]
        });
        using var transport = CreateTransport(fixture.Address, timeProvider: new FixedUtcTimeProvider(reference));
        ResponseHeadersObservation? headers = null;
        int headersObserved = 0;

        RequestObservation result = await transport.SendAsync(0, default, observed =>
        {
            headers = observed;
            headersObserved++;
        });

        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(kind, result.RetryAfter.Kind);
        Assert.Equal(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null, result.RetryAfter.Delay);
        Assert.Equal(clamped, result.RetryAfter.WasClamped);
        Assert.Equal(kind == RetryAfterKind.HttpDate ? reference : (DateTimeOffset?)null, result.RetryAfter.ReferenceUtc);
        Assert.Equal(1, headersObserved);
        Assert.Equal(result.RetryAfter, headers!.Value.RetryAfter);
    }

    [Fact]
    public async Task Duplicate_retry_after_fields_are_invalid_without_changing_http_completion()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync(new()
        {
            StatusCode = 429, RetryAfterValues = ["5", "7"]
        });
        using var transport = CreateTransport(fixture.Address);

        RequestObservation result = await transport.SendAsync(0);

        Assert.Equal(RequestOutcome.HttpCompleted, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(RetryAfterKind.Invalid, result.RetryAfter.Kind);
        Assert.Null(result.RetryAfter.Delay);
    }

    [Fact]
    public async Task Environment_proxy_is_ignored_in_an_isolated_cli_process()
    {
        await using var fixture = await ControlledHttpFixture.StartAsync();
        await using var proxy = await ControlledHttpFixture.StartAsync(new() { StatusCode = 502 });
        string temporary = Path.Combine(Path.GetTempPath(), "dinamico-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string config = Path.Combine(temporary, "config.json");
            string report = Path.Combine(temporary, "report.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, url = fixture.Address.AbsoluteUri, requestsPerSecond = 10,
                durationSeconds = 2, maxConcurrency = 1
            }));
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dinamico.slnx")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(Path.Combine(directory.FullName, "src", "Dinamico.LoadEngine.Local", "bin",
                configuration, "net10.0", "Dinamico.LoadEngine.Local.dll"));
            foreach (string argument in new[] { "run", "--config", config, "--output", report })
                start.ArgumentList.Add(argument);
            foreach (string name in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
                start.Environment[name] = proxy.Address.AbsoluteUri;
            start.Environment["NO_PROXY"] = "";
            start.Environment["no_proxy"] = "";
            using var process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }

            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Equal("", await stdout);
            Assert.True(fixture.ReceivedCount > 0, await File.ReadAllTextAsync(report));
            Assert.Equal(0, proxy.ReceivedCount);
            Assert.Equal(0, proxy.ConnectionCount);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var counters = json.RootElement.GetProperty("result").GetProperty("totals").GetProperty("counters");
            Assert.Equal(fixture.ReceivedCount, counters.GetProperty("HttpCompleted").GetInt64());
            Assert.Equal(fixture.ReceivedCount, counters.GetProperty("HttpSuccess").GetInt64());
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private sealed class FixedUtcTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private static LocalHttpTransport CreateTransport(Uri address, string method = "GET",
        HttpHeader[]? headers = null, byte[]? payload = null, int maxResponseBytes = 1_048_576,
        TimeProvider? timeProvider = null)
    {
        PlanValidationResult validation = LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = address.AbsoluteUri,
            Method = method,
            MaxResponseBytes = maxResponseBytes,
            MaxConcurrency = 1,
            Headers = headers ?? [],
            Payload = payload ?? []
        }, Guid.NewGuid());
        Assert.True(validation.IsValid, string.Join(", ", validation.Errors.Select(e => e.Code)));
        return new LocalHttpTransport(validation.Plan!, StopwatchMonotonicClock.Instance, timeProvider);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
