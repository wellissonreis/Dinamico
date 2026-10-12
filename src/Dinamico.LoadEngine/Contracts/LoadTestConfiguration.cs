namespace Dinamico.LoadEngine.Contracts;

/// <summary>Entrada não confiável. Não modificar arrays enquanto a validação está em andamento.</summary>
public sealed record LoadTestConfiguration
{
    public string? Url { get; init; }
    public string? Method { get; init; } = "GET";
    public long RequestsPerSecond { get; init; } = 100;
    public long DurationSeconds { get; init; } = 30;
    public int MaxConcurrency { get; init; } = LocalEngineLimits.DefaultConcurrency;
    public TimeSpan RequestTimeout { get; init; } = LocalEngineLimits.DefaultRequestTimeout;
    public int MaxResponseBytes { get; init; } = LocalEngineLimits.MaxResponseBytes;
    public HttpHeader[]? Headers { get; init; } = [];
    public byte[]? Payload { get; init; } = [];
    public RateLimitMode RateLimitMode { get; init; } = RateLimitMode.Normal;
    public LoadRetryPolicy RetryPolicy { get; init; } = LoadRetryPolicy.None;
    public HttpSuccessRule SuccessRule { get; init; } = HttpSuccessRule.Completed2xxV1;
    /// <summary>Índice em Headers; somente identificação local, não referência de cofre ou grant.</summary>
    public int? TestCredentialHeaderIndex { get; init; }
    public bool TestAuthorizationDeclared { get; init; }

    public override string ToString() => "LoadTestConfiguration { sensitive values omitted }";
}

public sealed record HttpHeader(string? Name, string? Value)
{
    public override string ToString() => "HttpHeader { sensitive values omitted }";
}
