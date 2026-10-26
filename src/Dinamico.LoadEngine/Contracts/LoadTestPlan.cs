using System.Collections.Immutable;

namespace Dinamico.LoadEngine.Contracts;

/// <summary>Snapshot validado: contém dados sensíveis; não serializar em logs ou relatórios.</summary>
public sealed class LoadTestPlan
{
    internal LoadTestPlan(Guid executionId, Uri target, string pinnedIpAddress,
        LoadTestConfiguration configuration, long plannedTotal)
    {
        ExecutionId = executionId;
        Target = target;
        PinnedIpAddress = pinnedIpAddress;
        Method = configuration.Method!;
        RequestsPerSecond = configuration.RequestsPerSecond;
        DurationSeconds = configuration.DurationSeconds;
        MaxConcurrency = configuration.MaxConcurrency;
        RequestTimeout = configuration.RequestTimeout;
        MaxResponseBytes = configuration.MaxResponseBytes;
        PlannedTotal = plannedTotal;
        RateLimitMode = configuration.RateLimitMode;
        RetryPolicy = configuration.RetryPolicy;
        SuccessRule = configuration.SuccessRule;
        TestCredentialHeaderIndex = configuration.TestCredentialHeaderIndex;
        TestAuthorizationDeclared = configuration.TestAuthorizationDeclared;
        CaptureMode = configuration.CaptureMode;
        CorrelateRequests = configuration.CorrelateRequests;
        // Cópias defensivas únicas por plano, nunca por requisição.
        Headers = ImmutableArray.CreateRange(configuration.Headers!);
        Payload = ImmutableArray.CreateRange(configuration.Payload!);
    }

    public int SchemaVersion => 2;
    public string HistogramSchema => "log2-32-v1";
    public Guid ExecutionId { get; }
    public Uri Target { get; }
    public string PinnedIpAddress { get; }
    public string Method { get; }
    public Version HttpVersion => System.Net.HttpVersion.Version11;
    public long RequestsPerSecond { get; }
    public long DurationSeconds { get; }
    public long PlannedTotal { get; }
    public int MaxConcurrency { get; }
    public TimeSpan RequestTimeout { get; }
    public int MaxResponseBytes { get; }
    public ImmutableArray<HttpHeader> Headers { get; }
    public ImmutableArray<byte> Payload { get; }
    public RateLimitMode RateLimitMode { get; }
    public LoadRetryPolicy RetryPolicy { get; }
    public HttpSuccessRule SuccessRule { get; }
    public int? TestCredentialHeaderIndex { get; }
    public bool TestAuthorizationDeclared { get; }
    public RequestCaptureMode CaptureMode { get; }
    public bool CorrelateRequests { get; }

    public int MaxConnectionsPerServer => MaxConcurrency;
    public int ReadBufferBytes => LocalEngineLimits.ReadBufferBytes;
    public int MaxRequestHeaderBytes => LocalEngineLimits.MaxRequestHeaderBytes;
    public int MaxResponseHeaderBytes => LocalEngineLimits.MaxResponseHeaderBytes;
    public TimeSpan ConnectTimeout => RequestTimeout < TimeSpan.FromSeconds(3)
        ? RequestTimeout : TimeSpan.FromSeconds(3);
    public TimeSpan PooledConnectionLifetime => TimeSpan.FromMinutes(2);
    public TimeSpan PooledConnectionIdleTimeout => TimeSpan.FromSeconds(30);

    public override string ToString() => $"LoadTestPlan {{ ExecutionId = {ExecutionId}, SchemaVersion = {SchemaVersion} }}";
}
