using Dinamico.LoadEngine.Contracts;
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

        Task<RequestObservation> pending = transport.SendAsync(3);
        await WaitUntilAsync(() => fixture.ReceivedCount == 1);
        // The fixture gates truncation until the client has observed headers. Its wait is bounded.
        await Task.Delay(50);
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
        Task<RequestObservation> pending = transport.SendAsync(0, stop.Token);
        await WaitUntilAsync(() => fixture.ReceivedCount == 1);
        await Task.Delay(60);
        stop.Cancel();

        RequestObservation result = await pending;
        Assert.Equal(RequestOutcome.CancelledInFlight, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(RetryAfterKind.DeltaSeconds, result.RetryAfter.Kind);
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

    private static LocalHttpTransport CreateTransport(Uri address, string method = "GET",
        HttpHeader[]? headers = null, byte[]? payload = null, int maxResponseBytes = 1_048_576)
    {
        PlanValidationResult validation = LocalPlanValidator.Validate(new LoadTestConfiguration
        {
            Url = address.AbsoluteUri,
            Method = method,
            MaxResponseBytes = maxResponseBytes,
            Headers = headers ?? [],
            Payload = payload ?? []
        }, Guid.NewGuid());
        Assert.True(validation.IsValid, string.Join(", ", validation.Errors.Select(e => e.Code)));
        return new LocalHttpTransport(validation.Plan!);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
