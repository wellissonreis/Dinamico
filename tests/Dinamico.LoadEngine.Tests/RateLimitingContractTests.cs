using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class RateLimitingContractTests
{
    private static readonly Guid ExecutionId = Guid.Parse("4dc15c1f-fb3a-4f8d-94b5-f14898136d50");
    private static LoadTestConfiguration Minimal => new() { Url = "http://127.0.0.1/" };
    private static PlanValidationResult Validate(LoadTestConfiguration config) => LocalPlanValidator.Validate(config, ExecutionId);

    [Fact]
    public void Defaults_are_explicit_in_schema_two()
    {
        var plan = Validate(Minimal).Plan!;
        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal(RateLimitMode.Normal, plan.RateLimitMode);
        Assert.Equal(LoadRetryPolicy.None, plan.RetryPolicy);
        Assert.Equal(HttpSuccessRule.Completed2xxV1, plan.SuccessRule);
        Assert.Null(plan.TestCredentialHeaderIndex);
        Assert.False(plan.TestAuthorizationDeclared);
    }

    [Theory]
    [InlineData(RateLimitMode.Normal)]
    [InlineData(RateLimitMode.Authorized)]
    [InlineData(RateLimitMode.Diagnostic)]
    public void Modes_preserve_profile_and_do_not_insert_bypass_headers(RateLimitMode mode)
    {
        HttpHeader[] headers = [new("Authorization", "Bearer synthetic-test-token")];
        var config = Minimal with { RateLimitMode = mode, Headers = headers,
            TestCredentialHeaderIndex = 0, TestAuthorizationDeclared = true };
        var plan = Validate(config).Plan!;
        var baseline = Validate(Minimal).Plan!;
        Assert.Equal(mode, plan.RateLimitMode);
        Assert.Equal(baseline.PlannedTotal, plan.PlannedTotal);
        Assert.Equal(baseline.RequestsPerSecond, plan.RequestsPerSecond);
        Assert.Equal(baseline.DurationSeconds, plan.DurationSeconds);
        Assert.Equal(baseline.MaxConcurrency, plan.MaxConcurrency);
        Assert.Equal(baseline.RequestTimeout, plan.RequestTimeout);
        Assert.Equal(LoadRetryPolicy.None, plan.RetryPolicy);
        Assert.Equal(headers, plan.Headers.ToArray());
        headers[0] = new("X-Changed", "replacement");
        Assert.Equal("Authorization", plan.Headers[plan.TestCredentialHeaderIndex!.Value].Name);
    }

    [Theory]
    [InlineData(RateLimitMode.Normal)]
    [InlineData(RateLimitMode.Diagnostic)]
    public void Normal_and_diagnostic_can_use_credentials_without_authorized_declaration(RateLimitMode mode)
    {
        Assert.True(Validate(Minimal with { RateLimitMode = mode,
            Headers = [new("X-Api-Key", "synthetic-key")] }).IsValid);
    }

    [Fact]
    public void Authorized_requires_both_local_credential_marker_and_declaration()
    {
        var result = Validate(Minimal with { RateLimitMode = RateLimitMode.Authorized,
            Headers = [new("Authorization", "Bearer synthetic-test-token")] });
        Assert.Contains(result.Errors, e => e.Code == ConfigurationErrorCode.TestCredentialRequired);
        Assert.Contains(result.Errors, e => e.Code == ConfigurationErrorCode.TestAuthorizationRequired);
        Assert.Null(result.Plan);
        Assert.False(Validate(Minimal with { RateLimitMode = RateLimitMode.Authorized,
            TestAuthorizationDeclared = true }).IsValid);
        Assert.False(Validate(Minimal with { RateLimitMode = RateLimitMode.Authorized,
            Headers = [new("X-Key", "synthetic")], TestCredentialHeaderIndex = 0 }).IsValid);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Invalid_credential_indices_are_rejected_in_all_modes(int index)
    {
        foreach (var mode in Enum.GetValues<RateLimitMode>())
            Assert.False(Validate(Minimal with { RateLimitMode = mode, TestAuthorizationDeclared = true,
                Headers = [new("X-Key", "synthetic")], TestCredentialHeaderIndex = index }).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Credential_header_must_have_a_nonempty_value(string? value)
    {
        Assert.False(Validate(Minimal with { Headers = [new("X-Key", value)], TestCredentialHeaderIndex = 0 }).IsValid);
    }

    [Fact]
    public void Missing_header_array_or_entry_never_throws()
    {
        Assert.False(Validate(Minimal with { Headers = null, TestCredentialHeaderIndex = 0 }).IsValid);
        Assert.False(Validate(Minimal with { Headers = [null!], TestCredentialHeaderIndex = 0 }).IsValid);
        Assert.False(Validate(Minimal with { Headers = [], TestCredentialHeaderIndex = 0 }).IsValid);
    }

    [Theory]
    [InlineData("Host", "synthetic")]
    [InlineData("Proxy-Authorization", "synthetic")]
    [InlineData("X-Key", "synthetic\r\nInjected: yes")]
    public void Authorized_mode_preserves_header_security(string name, string value)
    {
        Assert.False(Validate(Minimal with { RateLimitMode = RateLimitMode.Authorized,
            Headers = [new(name, value)], TestCredentialHeaderIndex = 0, TestAuthorizationDeclared = true }).IsValid);
    }

    [Fact]
    public void Authorized_mode_preserves_target_limits_and_case_insensitive_collisions()
    {
        var config = Minimal with { RateLimitMode = RateLimitMode.Authorized,
            Headers = [new("Authorization", "Bearer synthetic")], TestCredentialHeaderIndex = 0,
            TestAuthorizationDeclared = true };
        Assert.False(Validate(config with { Url = "https://example.invalid/" }).IsValid);
        Assert.False(Validate(config with { RequestsPerSecond = 1001 }).IsValid);
        Assert.Contains(Validate(config with { Headers = [new("Authorization", "a"), new("authorization", "b")] }).Errors,
            e => e.Code == ConfigurationErrorCode.DuplicateHeader);
    }

    [Fact]
    public void Unknown_policies_fail_closed_without_fallback()
    {
        var result = Validate(Minimal with { RateLimitMode = (RateLimitMode)999,
            RetryPolicy = (LoadRetryPolicy)1, SuccessRule = (HttpSuccessRule)999 });
        Assert.Null(result.Plan);
        Assert.Equal(3, result.Errors.Length);
        Assert.All(result.Errors, e => Assert.Equal(ConfigurationErrorCode.UnsupportedValue, e.Code));
    }

    [Theory]
    [InlineData(200, true, false)]
    [InlineData(202, true, false)]
    [InlineData(299, true, false)]
    [InlineData(300, false, false)]
    [InlineData(401, false, false)]
    [InlineData(403, false, false)]
    [InlineData(429, false, true)]
    [InlineData(500, false, false)]
    [InlineData(503, false, false)]
    public void Complete_http_acceptance_and_limiting_are_distinct(int status, bool accepted, bool limited)
    {
        var observation = new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.FromMilliseconds(1), status, 10);
        Assert.Equal(accepted, observation.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.Equal(limited, observation.IsRateLimited);
        Assert.NotNull(observation.HttpCompletedDuration);
        Assert.Equal(RetryAfterKind.NotObserved, observation.RetryAfter.Kind);
    }

    [Theory]
    [InlineData(RequestOutcome.TransportFailed, RequestErrorKind.ResponseRead)]
    [InlineData(RequestOutcome.RequestTimedOut, RequestErrorKind.RequestTimeout)]
    [InlineData(RequestOutcome.CancelledInFlight, RequestErrorKind.Cancellation)]
    [InlineData(RequestOutcome.ResponseLimitExceeded, RequestErrorKind.ResponseLimit)]
    [InlineData(RequestOutcome.InternalFailed, RequestErrorKind.Internal)]
    public void Rate_limit_evidence_survives_incomplete_body_with_one_terminal(RequestOutcome outcome, RequestErrorKind error)
    {
        var retryAfter = RetryAfterObservation.Parse(429, ["120"], DateTimeOffset.UnixEpoch);
        var limited = new RequestObservation(0, outcome, TimeSpan.FromSeconds(2), 429, 10, error, retryAfter);
        Assert.True(limited.IsRateLimited);
        Assert.False(limited.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.Null(limited.HttpCompletedDuration);
        Assert.Equal(outcome, limited.Outcome);
        Assert.Equal(retryAfter, limited.RetryAfter);
        var incomplete2xx = new RequestObservation(0, outcome, TimeSpan.FromSeconds(2), 200, 10, error);
        Assert.False(incomplete2xx.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.False(incomplete2xx.IsRateLimited);
    }

    [Fact]
    public void Invalid_retry_after_is_data_and_does_not_make_http_a_transport_failure()
    {
        var evidence = RetryAfterObservation.Parse(429, ["invalid"], DateTimeOffset.UnixEpoch);
        var observation = new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 429, 0,
            retryAfter: evidence);
        Assert.Equal(RequestOutcome.HttpCompleted, observation.Outcome);
        Assert.Equal(RetryAfterKind.Invalid, observation.RetryAfter.Kind);
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted,
            TimeSpan.Zero, 503, 0, retryAfter: evidence));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(103)]
    [InlineData(199)]
    public void Informational_status_is_not_a_final_response(int status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted,
            TimeSpan.Zero, status, 0));
    }

    [Fact]
    public void Default_observation_is_neither_accepted_nor_limited_and_unknown_rule_throws()
    {
        var observation = default(RequestObservation);
        Assert.False(observation.IsAccepted(HttpSuccessRule.Completed2xxV1));
        Assert.False(observation.IsRateLimited);
        Assert.Equal(RetryAfterKind.NotObserved, observation.RetryAfter.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => observation.IsAccepted((HttpSuccessRule)999));
    }

    [Fact]
    public void Canary_is_absent_from_available_diagnostic_surfaces()
    {
        const string canary = "rate-secret-canary-7392";
        var config = Minimal with { RateLimitMode = RateLimitMode.Authorized,
            Headers = [new("X-Unconventional-Key", canary)], TestCredentialHeaderIndex = 0,
            TestAuthorizationDeclared = true };
        var plan = Validate(config).Plan!;
        var rejected = Validate(config with { Headers = [new("X-Key", canary + "\r\n")] });
        var observation = new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 429, 0,
            retryAfter: RetryAfterObservation.Parse(429, [canary], DateTimeOffset.UnixEpoch));
        Assert.DoesNotContain(canary, config.ToString());
        Assert.DoesNotContain(canary, plan.ToString());
        Assert.DoesNotContain(canary, plan.Headers[0].ToString());
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(rejected.Errors));
        Assert.DoesNotContain(canary, string.Join(";", rejected.Errors));
        Assert.DoesNotContain(canary, JsonSerializer.Serialize(observation));
        Assert.DoesNotContain(canary, observation.ToString());
    }

    [Fact]
    public void Missing_header_requires_explicit_capture_and_cannot_mean_not_applicable()
    {
        var unknown = new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 429, 0);
        var absent = new RequestObservation(0, RequestOutcome.HttpCompleted, TimeSpan.Zero, 429, 0,
            retryAfter: RetryAfterObservation.Parse(429, [], DateTimeOffset.UnixEpoch));
        Assert.Equal(RetryAfterKind.NotObserved, unknown.RetryAfter.Kind);
        Assert.Equal(RetryAfterKind.Missing, absent.RetryAfter.Kind);
        var notApplicable = RetryAfterObservation.Parse(503, ["120"], DateTimeOffset.UnixEpoch);
        Assert.Throws<ArgumentException>(() => new RequestObservation(0, RequestOutcome.HttpCompleted,
            TimeSpan.Zero, 429, 0, retryAfter: notApplicable));
    }
}
