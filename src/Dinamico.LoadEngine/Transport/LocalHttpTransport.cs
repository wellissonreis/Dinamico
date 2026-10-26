using System.Collections.Immutable;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Diagnostics;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Timing;

namespace Dinamico.LoadEngine.Transport;

/// <summary>Reusable HTTP/1.1 transport for one validated loopback plan.</summary>
public sealed class LocalHttpTransport : IDisposable, ILocalRunTransport
{
    private readonly LoadTestPlan _plan;
    private readonly HttpClient _client;
    private readonly IMonotonicClock _clock;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public LocalHttpTransport(LoadTestPlan plan)
        : this(plan, StopwatchMonotonicClock.Instance, TimeProvider.System)
    {
    }

    public LocalHttpTransport(LoadTestPlan plan, IMonotonicClock clock, TimeProvider? timeProvider = null)
        : this(plan, clock, timeProvider ?? TimeProvider.System, null)
    {
    }

    internal LocalHttpTransport(LoadTestPlan plan, IMonotonicClock clock,
        TimeProvider timeProvider, HttpMessageHandler? messageHandler)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (plan.SchemaVersion != 2 || plan.RetryPolicy != LoadRetryPolicy.None
            || plan.SuccessRule != HttpSuccessRule.Completed2xxV1)
            throw new ArgumentException("Unsupported local plan contract.", nameof(plan));
        var handler = messageHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = plan.MaxConnectionsPerServer,
            MaxResponseHeadersLength = (plan.MaxResponseHeaderBytes + 1023) / 1024,
            ConnectTimeout = plan.ConnectTimeout,
            PooledConnectionLifetime = plan.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = plan.PooledConnectionIdleTimeout,
            ConnectCallback = ConnectPinnedLoopbackAsync,
            ActivityHeadersPropagator = null
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<RequestObservation> SendAsync(long arrivalIndex, CancellationToken cancellationToken = default) =>
        SendAsync(arrivalIndex, cancellationToken, null);

    /// <summary>Headers observer runs once before body consumption; failures propagate to the controller.</summary>
    public async Task<RequestObservation> SendAsync(long arrivalIndex, CancellationToken cancellationToken,
        Action<ResponseHeadersObservation>? observeHeaders)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(arrivalIndex);
        using var deadline = new OperationDeadline(_clock, _timeProvider, _plan.RequestTimeout, cancellationToken);
        int? status = null;
        long bodyBytes = 0;
        var retryAfter = default(RetryAfterObservation);
        long correlationStride = _plan.CaptureMode == RequestCaptureMode.Complete ? 1 : Math.Max(1, (_plan.PlannedTotal + 999) / 1000);
        bool correlate = _plan.CorrelateRequests && arrivalIndex % correlationStride == 0;
        string? traceId = correlate ? ActivityTraceId.CreateRandom().ToHexString() : null;
        string? spanId = correlate ? ActivitySpanId.CreateRandom().ToHexString() : null;

        try
        {
            using var request = CreateRequest();
            if (traceId is not null) request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{spanId}-01");
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            int receivedStatus = (int)response.StatusCode;
            if (receivedStatus is < 200 or > 599)
            {
                deadline.Complete(RequestOutcome.TransportFailed, RequestErrorKind.Protocol);
                var unsupported = deadline.Result;
                return new RequestObservation(arrivalIndex, unsupported.Outcome, unsupported.Duration,
                    null, bodyBytes, unsupported.Error);
            }
            status = receivedStatus;
            deadline.MarkHeaders();
            DateTimeOffset referenceUtc = _timeProvider.GetUtcNow();
            string[] retryValues = response.Headers.TryGetValues("Retry-After", out var values)
                ? values.Take(2).ToArray()
                : [];
            retryAfter = RetryAfterObservation.Parse(status.Value, retryValues, referenceUtc);
            if (observeHeaders is not null)
            {
                try
                {
                    observeHeaders(new ResponseHeadersObservation(arrivalIndex, status.Value, retryAfter,
                        deadline.HeadersTimestamp!.Value));
                }
                catch (Exception ex) { throw new TransportObserverException(ex); }
            }

            bool bodyless = _plan.Method == "HEAD" || status is 204 or 304;
            if (bodyless)
            {
                deadline.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
            }
            else if (response.Content.Headers.ContentLength is long declared && declared > _plan.MaxResponseBytes)
            {
                deadline.Complete(RequestOutcome.ResponseLimitExceeded, RequestErrorKind.ResponseLimit);
            }
            else
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(_plan.ReadBufferBytes);
                try
                {
                    await using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    while (true)
                    {
                        long remainingBeforeLimit = _plan.MaxResponseBytes - bodyBytes;
                        int readLimit = (int)Math.Min(_plan.ReadBufferBytes, Math.Max(1, remainingBeforeLimit + 1));
                        int read = await stream.ReadAsync(buffer.AsMemory(0, readLimit), deadline.Token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            deadline.Complete(RequestOutcome.HttpCompleted, RequestErrorKind.None);
                            break;
                        }
                        bodyBytes += read;
                        if (bodyBytes > _plan.MaxResponseBytes)
                        {
                            deadline.Complete(RequestOutcome.ResponseLimitExceeded, RequestErrorKind.ResponseLimit);
                            break;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (TransportObserverException) { throw; }
        catch (OperationCanceledException ex)
        {
            // Caller cancellation and the total deadline have already latched their reason.
            // SocketsHttpHandler's independent connection ceiling includes a TimeoutException.
            if (ex.InnerException is TimeoutException)
                deadline.Complete(RequestOutcome.RequestTimedOut, RequestErrorKind.RequestTimeout);
            else
                deadline.Complete(RequestOutcome.TransportFailed,
                    status.HasValue ? RequestErrorKind.ResponseRead : RequestErrorKind.Connect);
        }
        catch (HttpRequestException ex)
        {
            deadline.Complete(RequestOutcome.TransportFailed,
                ex.InnerException is AuthenticationException ? RequestErrorKind.Tls
                : status.HasValue ? RequestErrorKind.ResponseRead : RequestErrorKind.Connect);
        }
        catch (IOException)
        {
            deadline.Complete(RequestOutcome.TransportFailed,
                status.HasValue ? RequestErrorKind.ResponseRead : RequestErrorKind.Connect);
        }
        catch (Exception)
        {
            deadline.Complete(RequestOutcome.InternalFailed, RequestErrorKind.Internal);
        }

        var terminal = deadline.Result;
        double? headersMilliseconds = deadline.HeadersTimestamp is { } headersTimestamp
            ? _clock.GetElapsedTime(deadline.StartedTimestamp, headersTimestamp).TotalMilliseconds : null;
        return new RequestObservation(arrivalIndex, terminal.Outcome, terminal.Duration,
            status, bodyBytes, terminal.Error, retryAfter)
        {
            TraceId = traceId, SpanId = spanId, StartedTimestamp = deadline.StartedTimestamp,
            HeadersMilliseconds = headersMilliseconds <= terminal.Duration.TotalMilliseconds ? headersMilliseconds : null
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _client.Dispose();
    }

    private HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(new HttpMethod(_plan.Method), _plan.Target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        byte[] payload = ImmutableCollectionsMarshal.AsArray(_plan.Payload) ?? [];
        ByteArrayContent? content = payload.Length == 0 ? null : new ByteArrayContent(payload);
        foreach (HttpHeader header in _plan.Headers)
        {
            if (request.Headers.TryAddWithoutValidation(header.Name!, header.Value))
                continue;

            content ??= new ByteArrayContent([]);
            if (!content.Headers.TryAddWithoutValidation(header.Name!, header.Value))
            {
                request.Dispose();
                content.Dispose();
                throw new InvalidOperationException("Validated request header could not be applied.");
            }
        }
        request.Content = content;
        return request;
    }

    private ValueTask<Stream> ConnectPinnedLoopbackAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress pinnedAddress = IPAddress.Parse(_plan.PinnedIpAddress);
        if (!IPAddress.IsLoopback(pinnedAddress))
            throw new HttpRequestException("The validated target is no longer loopback.");
        var socket = new Socket(pinnedAddress.AddressFamily,
            SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        return ConnectAsync(socket, pinnedAddress, context.DnsEndPoint.Port, cancellationToken);
    }

    private static async ValueTask<Stream> ConnectAsync(Socket socket, IPAddress address, int port,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
