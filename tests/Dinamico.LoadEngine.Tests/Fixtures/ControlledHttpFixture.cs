using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Dinamico.LoadEngine.Tests.Fixtures;

public sealed record HttpFixtureOptions
{
    public int StatusCode { get; init; } = 200;
    public int? AlternateStatusCode { get; init; }
    public string[] RetryAfterValues { get; init; } = [];
    public TimeSpan HeaderDelay { get; init; } = TimeSpan.Zero;
    public TimeSpan ChunkDelay { get; init; } = TimeSpan.Zero;
    public int BodyBytes { get; init; } = 16;
    public int ChunkBytes { get; init; } = 1024;
    public bool TruncateBody { get; init; }
    public bool Redirect { get; init; }
    public string? SyntheticCredential { get; init; }
    public int CredentialStatusCode { get; init; } = 429;
    public int MaxConcurrentRequests { get; init; } = 32;

    // Record-generated output would include the credential and header values.
    public override string ToString() => nameof(HttpFixtureOptions);
}

public sealed class ControlledHttpFixture : IAsyncDisposable
{
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpFixtureOptions _options;
    private readonly byte[] _chunk;
    private readonly WebApplication _application;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private readonly TaskCompletionSource _truncate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Uri _address = null!;
    private long _receivedCount;
    private long _connectionCount;
    private long _activeRequests;

    private ControlledHttpFixture(HttpFixtureOptions options)
    {
        _options = options;
        _chunk = new byte[Math.Min(options.ChunkBytes, Math.Max(options.BodyBytes, 1))];
        Array.Fill(_chunk, (byte)'x');

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(ControlledHttpFixture).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        builder.WebHost.UseKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = Math.Max(128, options.MaxConcurrentRequests * 2);
            server.Limits.MaxConcurrentUpgradedConnections = 0;
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.MaxRequestBufferSize = 64 * 1024;
            // Flush headers and chunks through the transport before a deliberate abort.
            server.Limits.MaxResponseBufferSize = 0;
            // Intentional slow responses must survive Kestrel's default data-rate timeout.
            server.Limits.MinResponseDataRate = null;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Limits.MaxRequestHeaderCount = 64;
            server.Limits.MaxRequestLineSize = 4 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            server.Listen(IPAddress.Loopback, 0, endpoint =>
            {
                endpoint.Protocols = HttpProtocols.Http1;
                endpoint.Use(next => connection =>
                {
                    Interlocked.Increment(ref _connectionCount);
                    return next(connection);
                });
            });
        });

        _application = builder.Build();
        _application.Run(HandleRequestAsync);
    }

    // Call after the client has observed headers: an immediate TCP reset can discard them.
    public void ReleaseTruncation() => _truncate.TrySetResult();

    public Uri Address => _address;
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);
    public long ConnectionCount => Interlocked.Read(ref _connectionCount);
    public long ActiveRequests => Interlocked.Read(ref _activeRequests);
    public string Summary => FormattableString.Invariant(
        $"ControlledHttpFixture: received={ReceivedCount}, connections={ConnectionCount}, active={ActiveRequests}");

    public static async Task<ControlledHttpFixture> StartAsync(HttpFixtureOptions? options = null)
    {
        var fixture = new ControlledHttpFixture(SnapshotAndValidate(options ?? new HttpFixtureOptions()));
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture._application.StartAsync(startupTimeout.Token).ConfigureAwait(false);
            var addresses = fixture._application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count != 1 ||
                !Uri.TryCreate(addresses.Single(), UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttp || address.Host != "127.0.0.1" || address.Port <= 0)
            {
                throw new InvalidOperationException("The HTTP fixture did not acquire a loopback address.");
            }

            fixture._address = address;
            return fixture;
        }
        catch
        {
            // Even partially started hosts must release their listening sockets.
            // Preserve the startup error if disposal also fails.
            try
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            _shutdown.Cancel();
            using var timeout = new CancellationTokenSource(ShutdownTimeout);
            try
            {
                await _application.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Kestrel aborts outstanding connections when graceful shutdown expires.
            }
        }
        finally
        {
            try
            {
                await _application.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Dispose();
            }
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        long receipt = Interlocked.Increment(ref _receivedCount);
        if (!TryEnterRequest())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentLength = 0;
            return;
        }

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdown.Token, context.RequestAborted);
            CancellationToken token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            await DelayAsync(_options.HeaderDelay, token).ConfigureAwait(false);

            bool destination = _options.Redirect && context.Request.Path == "/destination";
            context.Response.StatusCode = destination ? StatusCodes.Status200OK : SelectStatusCode(context, receipt);
            if (_options.RetryAfterValues.Length != 0)
            {
                context.Response.Headers.RetryAfter = new StringValues(_options.RetryAfterValues);
            }

            if (_options.Redirect && !destination)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "/destination";
                context.Response.ContentLength = 0;
                return;
            }

            // These final statuses cannot carry the configured entity body.
            if (context.Response.StatusCode is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
            {
                return;
            }

            if (context.Response.StatusCode == StatusCodes.Status205ResetContent)
            {
                context.Response.ContentLength = 0;
                return;
            }

            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = _options.BodyBytes;
            await context.Response.StartAsync(token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
            if (HttpMethods.IsHead(context.Request.Method))
            {
                return;
            }

            // The destination always finishes successfully, including in a truncation scenario.
            bool truncate = _options.TruncateBody && !destination;
            int remaining = truncate ? _options.BodyBytes / 2 : _options.BodyBytes;
            while (remaining > 0)
            {
                await DelayAsync(_options.ChunkDelay, token).ConfigureAwait(false);
                int length = Math.Min(remaining, _chunk.Length);
                await context.Response.Body.WriteAsync(_chunk.AsMemory(0, length), token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
                remaining -= length;
            }

            if (truncate)
            {
                try
                {
                    await _truncate.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A forgotten test signal must not retain a handler indefinitely.
                }
                context.Abort();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (IOException)
        {
            context.Abort();
        }
        finally
        {
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    private bool TryEnterRequest()
    {
        long active = Interlocked.Read(ref _activeRequests);
        while (active < _options.MaxConcurrentRequests)
        {
            long observed = Interlocked.CompareExchange(ref _activeRequests, active + 1, active);
            if (observed == active)
            {
                return true;
            }

            active = observed;
        }

        return false;
    }

    private int SelectStatusCode(HttpContext context, long receipt)
    {
        if (_options.SyntheticCredential is not null &&
            context.Request.Headers.TryGetValue("X-Test-Credential", out var credential) &&
            credential.Count == 1 &&
            string.Equals(credential[0], _options.SyntheticCredential, StringComparison.Ordinal))
        {
            return _options.CredentialStatusCode;
        }

        return (receipt & 1) == 0 && _options.AlternateStatusCode is { } alternate
            ? alternate
            : _options.StatusCode;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken token) =>
        delay == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, token);

    private static HttpFixtureOptions SnapshotAndValidate(HttpFixtureOptions options)
    {
        // Bound work before copying a caller-owned array. Strings themselves are immutable.
        if (options.RetryAfterValues is null || options.RetryAfterValues.Length > 16)
        {
            throw InvalidOptions();
        }

        var snapshot = options with { RetryAfterValues = (string[])options.RetryAfterValues.Clone() };
        if (!IsFinalStatus(snapshot.StatusCode) ||
            snapshot.AlternateStatusCode is { } alternate && !IsFinalStatus(alternate) ||
            !IsFinalStatus(snapshot.CredentialStatusCode) ||
            snapshot.HeaderDelay < TimeSpan.Zero || snapshot.HeaderDelay > MaximumDelay ||
            snapshot.ChunkDelay < TimeSpan.Zero || snapshot.ChunkDelay > MaximumDelay ||
            snapshot.BodyBytes is < 0 or > 16 * 1024 * 1024 ||
            snapshot.ChunkBytes is < 1 or > 64 * 1024 ||
            snapshot.MaxConcurrentRequests is < 1 or > 1024 ||
            snapshot.TruncateBody && snapshot.BodyBytes == 0 ||
            snapshot.SyntheticCredential is { } credential && !IsSafeHeaderValue(credential, 1024))
        {
            throw InvalidOptions();
        }

        int headerBytes = 0;
        foreach (string value in snapshot.RetryAfterValues)
        {
            if (!IsSafeHeaderValue(value, 4096))
            {
                throw InvalidOptions();
            }

            headerBytes += value.Length + 15; // "Retry-After: " and CRLF.
            if (headerBytes > 8192)
            {
                throw InvalidOptions();
            }
        }

        return snapshot;
    }

    private static bool IsFinalStatus(int status) => status is >= 200 and <= 599;

    private static bool IsSafeHeaderValue(string? value, int maximumLength)
    {
        if (value is null || value.Length > maximumLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character != '\t' && (character < ' ' || character > '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static ArgumentException InvalidOptions() =>
        new("Invalid HTTP fixture options.", "options");
}
