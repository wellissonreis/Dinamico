namespace Dinamico.LoadEngine.Contracts;

/// <summary>Limites de entrada do marco local; não representam capacidade medida.</summary>
public static class LocalEngineLimits
{
    public const int MaxRequestsPerSecond = 1_000;
    public const int MaxDurationSeconds = 600;
    public const int MaxConcurrency = 256;
    public const int DefaultConcurrency = 64;
    public const int MaxPayloadBytes = 64 * 1_024;
    public const int MaxRequestHeaderBytes = 16 * 1_024;
    public const int MaxResponseBytes = 1_024 * 1_024;
    public const int MaxResponseHeaderBytes = 16 * 1_024;
    public const int MaxHeaderCount = 128;
    public const int MaxUrlCharacters = 8_192;
    public const int ReadBufferBytes = 16 * 1_024;
    public static TimeSpan DefaultRequestTimeout => TimeSpan.FromSeconds(10);
    public static TimeSpan MaxRequestTimeout => TimeSpan.FromSeconds(60);
}
