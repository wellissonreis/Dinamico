using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dinamico.LoadEngine.Tests.Fixtures;
using Xunit;

namespace Dinamico.LoadEngine.Tests;

// These exercise the fixture through a simple HTTP client, not the engine transport.
public sealed class HttpFixtureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BlockingDelay = TimeSpan.FromSeconds(30);
    private const string SyntheticCanary = "synthetic-fixture-canary-no-real-credential";

    [Fact]
    public async Task Defaults_start_with_empty_counts_and_return_a_small_complete_200()
    {
        await using var lease = await FixtureLease.StartAsync();
        var fixture = lease.Fixture;
        AssertLoopbackAddress(fixture.Address);
        Assert.Equal(0L, fixture.ReceivedCount);
        Assert.Equal(0L, fixture.ConnectionCount);
        Assert.Equal(0L, fixture.ActiveRequests);
        using var client = CreateClient();
        using var response = await client.GetAsync(fixture.Address).WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(16, (await response.Content.ReadAsByteArrayAsync().WaitAsync(TestTimeout)).Length);
        Assert.Equal(1L, fixture.ReceivedCount);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Configured_status_and_complete_body_are_observed_over_loopback(int status)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = status,
            BodyBytes = 37,
            ChunkBytes = 7
        });
        var fixture = lease.Fixture;
        using var client = CreateClient();

        AssertLoopbackAddress(fixture.Address);
        using var response = await client.GetAsync(fixture.Address).WaitAsync(TestTimeout);
        var body = await response.Content.ReadAsByteArrayAsync().WaitAsync(TestTimeout);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(37, body.Length);
        Assert.Equal(1L, fixture.ReceivedCount);
        Assert.True(fixture.ConnectionCount >= 1);
        await EventuallyAsync(() => fixture.ActiveRequests == 0);
    }

    [Fact]
    public async Task Parallel_fixtures_have_independent_ephemeral_ports_counts_and_cleanup()
    {
        // Start both hosts concurrently; retain either lease if the other startup fails.
        var firstStart = FixtureLease.StartAsync(new HttpFixtureOptions { StatusCode = 200 });
        var secondStart = FixtureLease.StartAsync(new HttpFixtureOptions { StatusCode = 202 });
        try
        {
            await Task.WhenAll(firstStart, secondStart).WaitAsync(TestTimeout);
            var first = (await firstStart.WaitAsync(TestTimeout)).Fixture;
            var second = (await secondStart.WaitAsync(TestTimeout)).Fixture;
            AssertLoopbackAddress(first.Address);
            AssertLoopbackAddress(second.Address);
            Assert.NotEqual(first.Address.Port, second.Address.Port);
            using var firstClient = CreateClient();
            using var secondClient = CreateClient();

            var firstRequests = Enumerable.Range(0, 4)
                .Select(_ => GetCompleteResponseAsync(firstClient, first.Address, 200));
            var secondRequests = Enumerable.Range(0, 7)
                .Select(_ => GetCompleteResponseAsync(secondClient, second.Address, 202));
            await Task.WhenAll(firstRequests.Concat(secondRequests)).WaitAsync(TestTimeout);

            Assert.Equal(4L, first.ReceivedCount);
            Assert.Equal(7L, second.ReceivedCount);
            Assert.True(first.ConnectionCount >= 1);
            Assert.True(second.ConnectionCount >= 1);
            await EventuallyAsync(() => first.ActiveRequests == 0 && second.ActiveRequests == 0);
        }
        finally
        {
            await Task.WhenAll(DisposeStartedFixtureAsync(firstStart), DisposeStartedFixtureAsync(secondStart))
                .WaitAsync(TestTimeout);
        }

        await AssertPortReleasedAsync((await firstStart.WaitAsync(TestTimeout)).Fixture.Address);
        await AssertPortReleasedAsync((await secondStart.WaitAsync(TestTimeout)).Fixture.Address);
    }

    [Fact]
    public async Task Continuous_429_counts_each_explicit_request()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions { StatusCode = 429 });
        using var client = CreateClient();

        for (var receipt = 1; receipt <= 6; receipt++)
        {
            await GetCompleteResponseAsync(client, lease.Fixture.Address, 429);
            Assert.Equal((long)receipt, lease.Fixture.ReceivedCount);
        }
    }

    [Fact]
    public async Task Intermittent_429_uses_even_receipts_for_alternate_status()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429,
            AlternateStatusCode = 202
        });
        using var client = CreateClient();

        for (var receipt = 1; receipt <= 6; receipt++)
        {
            await GetCompleteResponseAsync(client, lease.Fixture.Address, receipt % 2 == 0 ? 202 : 429);
            Assert.Equal((long)receipt, lease.Fixture.ReceivedCount);
        }
    }

    [Fact]
    public async Task Synthetic_credential_is_ordinal_and_matching_status_overrides_alternation()
    {
        var options = new HttpFixtureOptions
        {
            StatusCode = 200,
            AlternateStatusCode = 202,
            SyntheticCredential = SyntheticCanary,
            CredentialStatusCode = 429
        };
        await using var lease = await FixtureLease.StartAsync(options);
        using var client = CreateClient();
        Assert.DoesNotContain(SyntheticCanary, options.ToString());
        Assert.DoesNotContain(SyntheticCanary, lease.Fixture.Summary);

        await GetCompleteResponseAsync(client, lease.Fixture.Address, 200);
        await GetCompleteResponseAsync(client, lease.Fixture.Address, 429, SyntheticCanary);
        await GetCompleteResponseAsync(client, lease.Fixture.Address, 200, SyntheticCanary.ToUpperInvariant());
        await GetCompleteResponseAsync(client, lease.Fixture.Address, 202, "synthetic-unmatched-canary");
        await GetCompleteResponseAsync(client, lease.Fixture.Address, 429, SyntheticCanary);
        Assert.Equal(5L, lease.Fixture.ReceivedCount);
        Assert.DoesNotContain(SyntheticCanary, options.ToString());
        Assert.DoesNotContain(SyntheticCanary, lease.Fixture.Summary);
        Assert.DoesNotContain("synthetic-unmatched-canary", lease.Fixture.Summary);

        await lease.DisposeAsync();
        Assert.DoesNotContain(SyntheticCanary, lease.Fixture.Summary);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(503)]
    public async Task Synthetic_credential_can_select_other_configured_statuses(int credentialStatus)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429,
            SyntheticCredential = SyntheticCanary,
            CredentialStatusCode = credentialStatus
        });
        using var client = CreateClient();

        await GetCompleteResponseAsync(client, lease.Fixture.Address, credentialStatus, SyntheticCanary);
        await GetCompleteResponseAsync(client, lease.Fixture.Address, 429);
        Assert.Equal(2L, lease.Fixture.ReceivedCount);
    }

    public static IEnumerable<object[]> RetryAfterCases()
    {
        yield return new object[] { Array.Empty<string>() };
        yield return new object[] { new[] { "0" } };
        yield return new object[] { new[] { "17" } };
        yield return new object[] { new[] { "Sun, 06 Nov 1994 08:49:37 GMT" } };
        yield return new object[] { new[] { "Thu, 01 Jan 2099 00:00:00 GMT" } };
        yield return new object[] { new[] { "invalid-retry-after" } };
        yield return new object[] { new[] { "7", "13" } };
        yield return new object[] { new[] { "9999999999999999999999999999999999999999" } };
    }

    [Theory]
    [MemberData(nameof(RetryAfterCases))]
    public async Task Retry_after_is_captured_as_raw_response_header_values(string[] values)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 429,
            RetryAfterValues = values
        });
        using var client = CreateClient();
        using var response = await client.GetAsync(lease.Fixture.Address).WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var found = response.Headers.TryGetValues("Retry-After", out var captured);
        Assert.Equal(values.Length != 0, found);
        Assert.Equal(values, captured?.ToArray() ?? Array.Empty<string>());
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
    }

    [Fact]
    public async Task Retry_after_on_503_preserves_503_status()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = 503,
            RetryAfterValues = new[] { "11" }
        });
        using var client = CreateClient();
        using var response = await client.GetAsync(lease.Fixture.Address).WaitAsync(TestTimeout);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal(new[] { "11" }, values.ToArray());
    }

    [Fact]
    public async Task Retry_after_options_are_snapshotted_before_caller_mutates_array()
    {
        var headers = new[] { "19" };
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions { RetryAfterValues = headers });
        headers[0] = "synthetic-mutated-header-canary";
        using var client = CreateClient();
        using var response = await client.GetAsync(lease.Fixture.Address).WaitAsync(TestTimeout);

        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal(new[] { "19" }, values.ToArray());
        Assert.DoesNotContain(headers[0], lease.Fixture.Summary);
    }

    [Fact]
    public async Task Summary_and_options_do_not_echo_synthetic_header_values()
    {
        const string headerCanary = "synthetic-retry-after-header-canary";
        var options = new HttpFixtureOptions
        {
            RetryAfterValues = new[] { headerCanary },
            SyntheticCredential = SyntheticCanary
        };
        await using var lease = await FixtureLease.StartAsync(options);
        using var client = CreateClient();
        Assert.DoesNotContain(headerCanary, options.ToString());
        Assert.DoesNotContain(SyntheticCanary, options.ToString());
        Assert.DoesNotContain(headerCanary, lease.Fixture.Summary);

        await GetCompleteResponseAsync(client, lease.Fixture.Address, 429, SyntheticCanary);
        Assert.DoesNotContain(headerCanary, lease.Fixture.Summary);
        Assert.DoesNotContain(SyntheticCanary, lease.Fixture.Summary);
        await lease.DisposeAsync();
        Assert.DoesNotContain(headerCanary, lease.Fixture.Summary);
        Assert.DoesNotContain(SyntheticCanary, lease.Fixture.Summary);
    }

    [Fact]
    public async Task Header_delay_can_be_cancelled_after_independent_receipt()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions { HeaderDelay = BlockingDelay });
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var request = client.GetAsync(lease.Fixture.Address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EventuallyAsync(() => lease.Fixture.ReceivedCount == 1 && lease.Fixture.ActiveRequests == 1);

        Assert.False(request.IsCompleted);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var response = await request.WaitAsync(TestTimeout);
        });
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
        await EventuallyAsync(() => lease.Fixture.ActiveRequests == 0);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    public async Task Slow_body_read_can_be_cancelled_without_losing_received_status(int status)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = status,
            BodyBytes = 1024,
            ChunkBytes = 1,
            ChunkDelay = BlockingDelay
        });
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var response = await client.GetAsync(lease.Fixture.Address, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).WaitAsync(TestTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(TestTimeout);
        var read = stream.ReadExactlyAsync(new byte[1024], deadline.Token).AsTask();
        await EventuallyAsync(() => lease.Fixture.ActiveRequests == 1);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.False(read.IsCompleted);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TestTimeout));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
        await EventuallyAsync(() => lease.Fixture.ActiveRequests == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_cancels_delayed_handlers_and_releases_listening_socket(bool slowBody)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            HeaderDelay = slowBody ? TimeSpan.Zero : BlockingDelay,
            ChunkDelay = slowBody ? BlockingDelay : TimeSpan.Zero,
            BodyBytes = 1024,
            ChunkBytes = 1
        });
        var fixture = lease.Fixture;
        var address = fixture.Address;
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var request = client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EventuallyAsync(() => fixture.ReceivedCount == 1 && fixture.ActiveRequests == 1);

        if (slowBody)
        {
            using var response = await request.WaitAsync(TestTimeout);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(TestTimeout);
            var read = stream.ReadExactlyAsync(new byte[1024], deadline.Token).AsTask();
            Assert.False(read.IsCompleted);
            await lease.DisposeAsync();
            await AssertBodyReadFailureAsync(read);
        }
        else
        {
            Assert.False(request.IsCompleted);
            await lease.DisposeAsync();
            var failure = await Record.ExceptionAsync(async () =>
            {
                using var response = await request.WaitAsync(TestTimeout);
            });
            Assert.NotNull(failure);
            Assert.True(failure is HttpRequestException or System.IO.IOException,
                $"Expected shutdown to terminate the request; got {failure.GetType().Name}.");
        }

        Assert.Equal(0L, fixture.ActiveRequests);
        Assert.Equal(1L, fixture.ReceivedCount);
        await AssertPortReleasedAsync(address);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(429)]
    public async Task Truncation_preserves_headers_and_status_but_body_read_fails(int status)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            StatusCode = status,
            BodyBytes = 8192,
            ChunkBytes = 1024,
            TruncateBody = true,
            RetryAfterValues = new[] { "3" }
        });
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var response = await client.GetAsync(lease.Fixture.Address, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).WaitAsync(TestTimeout);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(8192L, response.Content.Headers.ContentLength);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Equal(new[] { "3" }, values.ToArray());
        lease.Fixture.ReleaseTruncation();
        await AssertBodyReadFailureAsync(response.Content.ReadAsByteArrayAsync(deadline.Token));
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(17, 65536)]
    [InlineData(16 * 1024 * 1024, 65536)]
    public async Task Bounded_body_sizes_stream_to_the_declared_length(int bodyBytes, int chunkBytes)
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            BodyBytes = bodyBytes,
            ChunkBytes = chunkBytes
        });
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var response = await client.GetAsync(lease.Fixture.Address, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).WaitAsync(TestTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).WaitAsync(TestTimeout);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        {
            total += read;
            Assert.True(total <= bodyBytes);
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((long)bodyBytes, total);
        Assert.Equal((long)bodyBytes, response.Content.Headers.ContentLength);
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
    }

    [Fact]
    public async Task Redirect_stays_local_and_destination_completes_without_a_cycle()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            Redirect = true,
            StatusCode = 429
        });
        using var client = CreateClient();
        using var redirect = await client.GetAsync(lease.Fixture.Address).WaitAsync(TestTimeout);

        Assert.InRange((int)redirect.StatusCode, 300, 399);
        Assert.Equal(1L, lease.Fixture.ReceivedCount);
        Assert.NotNull(redirect.Headers.Location);
        var destination = new Uri(lease.Fixture.Address, redirect.Headers.Location);
        Assert.Equal(lease.Fixture.Address.Scheme, destination.Scheme);
        Assert.Equal(lease.Fixture.Address.Host, destination.Host);
        Assert.Equal(lease.Fixture.Address.Port, destination.Port);
        Assert.Equal("/destination", destination.AbsolutePath);

        using var response = await client.GetAsync(destination).WaitAsync(TestTimeout);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(2L, lease.Fixture.ReceivedCount);
    }

    [Fact]
    public async Task Concurrent_handler_limit_rejects_overflow_with_503_and_counts_receipt()
    {
        await using var lease = await FixtureLease.StartAsync(new HttpFixtureOptions
        {
            MaxConcurrentRequests = 1,
            HeaderDelay = BlockingDelay
        });
        using var client = CreateClient();
        using var deadline = new CancellationTokenSource(TestTimeout);
        var held = client.GetAsync(lease.Fixture.Address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await EventuallyAsync(() => lease.Fixture.ActiveRequests == 1 && lease.Fixture.ReceivedCount == 1);

        using var overflow = await client.GetAsync(lease.Fixture.Address).WaitAsync(TestTimeout);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, overflow.StatusCode);
        Assert.Equal(2L, lease.Fixture.ReceivedCount);
        Assert.Equal(1L, lease.Fixture.ActiveRequests);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var response = await held.WaitAsync(TestTimeout);
        });
        await EventuallyAsync(() => lease.Fixture.ActiveRequests == 0);
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return new object[] { new HttpFixtureOptions { StatusCode = -1 } };
        yield return new object[] { new HttpFixtureOptions { StatusCode = 600 } };
        yield return new object[] { new HttpFixtureOptions { AlternateStatusCode = -1 } };
        yield return new object[] { new HttpFixtureOptions { AlternateStatusCode = 600 } };
        yield return new object[] { new HttpFixtureOptions { CredentialStatusCode = -1 } };
        yield return new object[] { new HttpFixtureOptions { CredentialStatusCode = 600 } };
        yield return new object[] { new HttpFixtureOptions { BodyBytes = -1 } };
        yield return new object[] { new HttpFixtureOptions { BodyBytes = 16 * 1024 * 1024 + 1 } };
        yield return new object[] { new HttpFixtureOptions { ChunkBytes = 0 } };
        yield return new object[] { new HttpFixtureOptions { ChunkBytes = -1 } };
        yield return new object[] { new HttpFixtureOptions { ChunkBytes = 65537 } };
        yield return new object[] { new HttpFixtureOptions { HeaderDelay = TimeSpan.FromTicks(-1) } };
        yield return new object[] { new HttpFixtureOptions { HeaderDelay = BlockingDelay + TimeSpan.FromTicks(1) } };
        yield return new object[] { new HttpFixtureOptions { ChunkDelay = TimeSpan.FromTicks(-1) } };
        yield return new object[] { new HttpFixtureOptions { ChunkDelay = BlockingDelay + TimeSpan.FromTicks(1) } };
        yield return new object[] { new HttpFixtureOptions { HeaderDelay = TimeSpan.MaxValue } };
        yield return new object[] { new HttpFixtureOptions { ChunkDelay = TimeSpan.MaxValue } };
        yield return new object[] { new HttpFixtureOptions { MaxConcurrentRequests = 0 } };
        yield return new object[] { new HttpFixtureOptions { MaxConcurrentRequests = -1 } };
        yield return new object[] { new HttpFixtureOptions { MaxConcurrentRequests = int.MaxValue } };
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public async Task Invalid_limits_fail_startup_without_exposing_synthetic_configuration(HttpFixtureOptions options)
    {
        var configured = options with { SyntheticCredential = SyntheticCanary };
        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var lease = await FixtureLease.StartAsync(configured);
        });

        Assert.NotNull(failure);
        Assert.IsNotType<TimeoutException>(failure);
        Assert.DoesNotContain(SyntheticCanary, failure.ToString());
        Assert.DoesNotContain(SyntheticCanary, configured.ToString());
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        Timeout = TestTimeout,
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    private static void AssertLoopbackAddress(Uri address)
    {
        Assert.Equal(Uri.UriSchemeHttp, address.Scheme);
        Assert.Equal(IPAddress.Loopback.ToString(), address.Host);
        Assert.InRange(address.Port, 1, 65535);
    }

    private static async Task AssertPortReleasedAsync(Uri address)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = new CancellationTokenSource(TestTimeout);
        var refusal = await Assert.ThrowsAsync<SocketException>(() =>
            probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, address.Port), deadline.Token).AsTask());
        Assert.Equal(SocketError.ConnectionRefused, refusal.SocketErrorCode);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // The refused connection above proves no listener remains, including on Windows.
        // Match the server's reuse semantics so accepted-connection TIME_WAIT is not
        // mistaken for a live listener. A still-open listening socket prevents this bind.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, address.Port));
        socket.Listen(1);
    }

    private static async Task GetCompleteResponseAsync(HttpClient client, Uri address, int status,
        string? syntheticCredential = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (syntheticCredential is not null)
            request.Headers.Add("X-Test-Credential", syntheticCredential);
        using var response = await client.SendAsync(request).WaitAsync(TestTimeout);
        Assert.Equal(status, (int)response.StatusCode);
        await response.Content.ReadAsByteArrayAsync().WaitAsync(TestTimeout);
    }

    private static async Task AssertBodyReadFailureAsync(Task read)
    {
        var failure = await Record.ExceptionAsync(() => read.WaitAsync(TestTimeout));
        Assert.NotNull(failure);
        // A deadline or caller cancellation must not pass as evidence of truncation/shutdown.
        Assert.True(failure is HttpRequestException or System.IO.IOException,
            $"Expected a body transport failure; got {failure.GetType().Name}.");
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }

    private static async Task DisposeStartedFixtureAsync(Task<FixtureLease> startup)
    {
        FixtureLease lease;
        try
        {
            lease = await startup.WaitAsync(TestTimeout);
        }
        catch
        {
            // The startup failure is surfaced by WhenAll; no lease exists to dispose.
            return;
        }

        await lease.DisposeAsync();
    }

    private sealed class FixtureLease : IAsyncDisposable
    {
        private int disposed;

        private FixtureLease(ControlledHttpFixture fixture) => Fixture = fixture;

        public ControlledHttpFixture Fixture { get; }

        public static async Task<FixtureLease> StartAsync(HttpFixtureOptions? options = null) =>
            new(await ControlledHttpFixture.StartAsync(options).WaitAsync(TestTimeout));

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await Fixture.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
    }
}
