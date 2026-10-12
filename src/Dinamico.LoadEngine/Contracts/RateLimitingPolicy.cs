namespace Dinamico.LoadEngine.Contracts;

/// <summary>Todos os modos preservam a carga planejada; nenhum concede bypass.</summary>
public enum RateLimitMode
{
    Normal = 0,
    Authorized,
    Diagnostic
}

/// <summary>Retries de carga não são suportados. Retry de telemetria é outro contrato.</summary>
public enum LoadRetryPolicy
{
    None = 0
}

/// <summary>Aceitação HTTP não comprova processamento de negócio.</summary>
public enum HttpSuccessRule
{
    Completed2xxV1 = 0
}
