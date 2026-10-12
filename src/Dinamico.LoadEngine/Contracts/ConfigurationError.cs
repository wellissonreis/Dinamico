using System.Collections.Immutable;

namespace Dinamico.LoadEngine.Contracts;

public enum ConfigurationErrorCode
{
    Required = 1,
    OutOfRange,
    InvalidUrl,
    SchemeNotAllowed,
    LoopbackLiteralRequired,
    UserInfoNotAllowed,
    MethodNotAllowed,
    InvalidHeader,
    HeaderNotAllowed,
    DuplicateHeader,
    HeadersTooLarge,
    PayloadTooLarge,
    PlannedTotalOverflow,
    UnsupportedValue,
    TestCredentialRequired,
    TestAuthorizationRequired
}

public enum ConfigurationField
{
    Configuration = 1,
    ExecutionId,
    Url,
    Method,
    RequestsPerSecond,
    DurationSeconds,
    MaxConcurrency,
    RequestTimeout,
    MaxResponseBytes,
    Headers,
    Payload,
    PlannedTotal,
    RateLimitMode,
    RetryPolicy,
    SuccessRule,
    TestCredentialHeaderIndex,
    TestAuthorizationDeclared
}

/// <summary>Apenas campos controlados pelo motor: nenhum valor da entrada vai para o erro.</summary>
public readonly record struct ConfigurationError(
    ConfigurationErrorCode Code, ConfigurationField Field, int? HeaderIndex = null)
{
    public string EventName => "configuration.rejected";
}

public sealed class PlanValidationResult
{
    private PlanValidationResult(LoadTestPlan? plan, ImmutableArray<ConfigurationError> errors)
    {
        Plan = plan;
        Errors = errors;
    }

    public bool IsValid => Plan is not null;
    public LoadTestPlan? Plan { get; }
    public ImmutableArray<ConfigurationError> Errors { get; }

    internal static PlanValidationResult Accepted(LoadTestPlan plan) => new(plan, []);
    internal static PlanValidationResult Rejected(IEnumerable<ConfigurationError> errors) =>
        new(null, ImmutableArray.CreateRange(errors));
}
