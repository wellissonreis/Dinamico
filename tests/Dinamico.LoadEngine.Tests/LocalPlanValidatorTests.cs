using System.Text.Json;
using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class LocalPlanValidatorTests
{
    private static readonly Guid ExecutionId = Guid.Parse("95c8d677-cc52-4939-832b-676598b7d19d");
    private static LoadTestConfiguration Minimal => new() { Url = "http://127.0.0.1:5000/products" };

    [Fact]
    public void Minimal_configuration_produces_versioned_plan_with_effective_defaults()
    {
        var result = LocalPlanValidator.Validate(Minimal, ExecutionId);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        var plan = Assert.IsType<LoadTestPlan>(result.Plan);
        Assert.Equal(ExecutionId, plan.ExecutionId);
        Assert.Equal(2, plan.SchemaVersion);
        Assert.Equal("log2-32-v1", plan.HistogramSchema);
        Assert.Equal("GET", plan.Method);
        Assert.Equal(new Version(1, 1), plan.HttpVersion);
        Assert.Equal(100, plan.RequestsPerSecond);
        Assert.Equal(30, plan.DurationSeconds);
        Assert.Equal(3_000, plan.PlannedTotal);
        Assert.Equal(64, plan.MaxConcurrency);
        Assert.Equal(64, plan.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), plan.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), plan.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.PooledConnectionIdleTimeout);
        Assert.Equal(1_048_576, plan.MaxResponseBytes);
        Assert.Equal(16_384, plan.ReadBufferBytes);
        Assert.Equal(16_384, plan.MaxRequestHeaderBytes);
        Assert.Equal(16_384, plan.MaxResponseHeaderBytes);
        Assert.Empty(plan.Headers);
        Assert.Empty(plan.Payload);
    }

    [Theory]
    [InlineData("http://127.0.0.1/", "127.0.0.1")]
    [InlineData("http://127.2.3.4:8080/path?q=1", "127.2.3.4")]
    [InlineData("https://[::1]:8443/", "::1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/", "::1")]
    [InlineData("http://[::ffff:127.0.0.1]/", "127.0.0.1")]
    public void Loopback_literals_are_accepted_without_dns(string url, string pinnedIp)
    {
        var result = LocalPlanValidator.Validate(Minimal with { Url = url }, ExecutionId);
        Assert.True(result.IsValid);
        Assert.Equal(pinnedIp, result.Plan!.PinnedIpAddress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://127.0.0.1/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    [InlineData("http://[::1%251]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://127.1/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://127.0.0.1:0/")]
    [InlineData("http://127.0.0.1:65536/")]
    [InlineData("http://127.0.0.1/#fragment")]
    [InlineData("http://user:secret@127.0.0.1/")]
    [InlineData("http://@127.0.0.1/")]
    [InlineData("http://127.0.0.1@evil.invalid/")]
    [InlineData("http://127.0.0.1/\r\nInjected: value")]
    [InlineData("http://127.0.0.1\\evil.invalid/")]
    [InlineData(" http://127.0.0.1/")]
    [InlineData("http://[::1")]
    public void Invalid_or_non_literal_destinations_never_produce_a_plan(string? url)
    {
        AssertRejected(Minimal with { Url = url }, ConfigurationField.Url);
    }

    [Theory]
    [InlineData("https://app-hub.cplus.com.br/")]
    [InlineData("http://localhost:5000/")]
    [InlineData("https://example.invalid/path?q=1")]
    public void Dns_targets_are_accepted_without_resolution(string url)
    {
        var result = LocalPlanValidator.Validate(Minimal with { Url = url }, ExecutionId);
        Assert.True(result.IsValid);
        Assert.Equal(new Uri(url), result.Plan!.Target);
    }

    [Fact]
    public void Url_length_is_bounded()
    {
        const string prefix = "http://127.0.0.1/";
        string url = prefix + new string('a', LocalEngineLimits.MaxUrlCharacters - prefix.Length);
        Assert.True(LocalPlanValidator.Validate(Minimal with { Url = url }, ExecutionId).IsValid);
        AssertRejected(Minimal with { Url = url + "a" }, ConfigurationField.Url);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public void Allowed_methods_are_preserved(string method)
    {
        Assert.Equal(method, LocalPlanValidator.Validate(Minimal with { Method = method }, ExecutionId).Plan!.Method);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("get")]
    [InlineData("CONNECT")]
    [InlineData("TRACE")]
    [InlineData("CUSTOM")]
    [InlineData("GET\r\nInjected")]
    public void Unapproved_methods_are_rejected(string? method)
    {
        AssertRejected(Minimal with { Method = method }, ConfigurationField.Method);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(long.MaxValue)]
    public void Rate_is_positive_and_bounded(long rate) =>
        AssertRejected(Minimal with { RequestsPerSecond = rate }, ConfigurationField.RequestsPerSecond);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(601)]
    [InlineData(long.MaxValue)]
    public void Duration_is_positive_and_bounded(long seconds) =>
        AssertRejected(Minimal with { DurationSeconds = seconds }, ConfigurationField.DurationSeconds);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(257)]
    public void Concurrency_is_positive_and_bounded(int limit) =>
        AssertRejected(Minimal with { MaxConcurrency = limit }, ConfigurationField.MaxConcurrency);

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60001)]
    public void Deadline_is_positive_and_at_most_sixty_seconds(int milliseconds) =>
        AssertRejected(Minimal with { RequestTimeout = TimeSpan.FromMilliseconds(milliseconds) }, ConfigurationField.RequestTimeout);

    [Fact]
    public void Connect_timeout_never_exceeds_total_deadline()
    {
        var plan = LocalPlanValidator.Validate(Minimal with { RequestTimeout = TimeSpan.FromMilliseconds(500) }, ExecutionId).Plan!;
        Assert.Equal(plan.RequestTimeout, plan.ConnectTimeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1048577)]
    public void Response_size_is_positive_and_bounded(int size) =>
        AssertRejected(Minimal with { MaxResponseBytes = size }, ConfigurationField.MaxResponseBytes);

    [Fact]
    public void Inclusive_limits_and_planned_total_are_deterministic()
    {
        var config = Minimal with { RequestsPerSecond = 1000, DurationSeconds = 600, MaxConcurrency = 256,
            RequestTimeout = TimeSpan.FromSeconds(60), Payload = new byte[65_536] };
        var first = LocalPlanValidator.Validate(config, ExecutionId);
        var second = LocalPlanValidator.Validate(config, ExecutionId);
        Assert.True(first.IsValid);
        Assert.Equal(600_000, first.Plan!.PlannedTotal);
        Assert.Equal(first.Plan.PlannedTotal, second.Plan!.PlannedTotal);
        Assert.Equal(first.Plan.ExecutionId, second.Plan.ExecutionId);
        Assert.True(LocalPlanValidator.Validate(Minimal with { RequestsPerSecond = 1, DurationSeconds = 1,
            MaxConcurrency = 1, MaxResponseBytes = 1, RequestTimeout = TimeSpan.FromTicks(1) }, ExecutionId).IsValid);
    }

    [Fact]
    public void Overflow_is_an_explicit_validation_error_not_wrapped_arithmetic()
    {
        var result = LocalPlanValidator.Validate(Minimal with { RequestsPerSecond = long.MaxValue, DurationSeconds = 2 }, ExecutionId);
        Assert.False(result.IsValid);
        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Code == ConfigurationErrorCode.PlannedTotalOverflow);
    }

    [Fact]
    public void Required_input_id_and_arrays_are_validated()
    {
        Assert.False(LocalPlanValidator.Validate(null, ExecutionId).IsValid);
        Assert.False(LocalPlanValidator.Validate(Minimal, Guid.Empty).IsValid);
        AssertRejected(Minimal with { Headers = null }, ConfigurationField.Headers);
        AssertRejected(Minimal with { Payload = null }, ConfigurationField.Payload);
        AssertRejected(Minimal with { Payload = new byte[65_537] }, ConfigurationField.Payload);
    }

    [Fact]
    public void Independent_errors_are_returned_together()
    {
        var result = LocalPlanValidator.Validate(Minimal with { RequestsPerSecond = 0, DurationSeconds = -1, Method = "TRACE" }, ExecutionId);
        Assert.Equal(3, result.Errors.Length);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Validation_diagnostics_cannot_echo_secrets()
    {
        const string secret = "secret-canary-578";
        var config = Minimal with { Url = $"http://{secret}@127.0.0.1/?token={secret}",
            Method = secret, Headers = [new(secret + "\r\n", secret)], Payload = System.Text.Encoding.UTF8.GetBytes(secret) };
        var result = LocalPlanValidator.Validate(config, ExecutionId);
        Assert.False(result.IsValid);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Errors));
        Assert.DoesNotContain(secret, string.Join(";", result.Errors));
        Assert.DoesNotContain(secret, config.ToString());
        Assert.DoesNotContain(secret, config.Headers![0].ToString());
        Assert.All(result.Errors, e => Assert.Equal("configuration.rejected", e.EventName));
        var accepted = LocalPlanValidator.Validate(Minimal with { Url = $"http://127.0.0.1/?token={secret}" }, ExecutionId);
        Assert.DoesNotContain(secret, accepted.Plan!.ToString());
    }

    private static void AssertRejected(LoadTestConfiguration config, ConfigurationField field)
    {
        var result = LocalPlanValidator.Validate(config, ExecutionId);
        Assert.False(result.IsValid);
        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Field == field);
    }
}
