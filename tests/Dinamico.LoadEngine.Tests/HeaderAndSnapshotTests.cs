using Dinamico.LoadEngine.Contracts;
using Dinamico.LoadEngine.Validation;

namespace Dinamico.LoadEngine.Tests;

public sealed class HeaderAndSnapshotTests
{
    private static PlanValidationResult Validate(params HttpHeader[] headers) => LocalPlanValidator.Validate(
        new() { Url = "http://127.0.0.1/", Headers = headers }, Guid.Parse("8a5a2259-69fe-45f1-9e0f-84f8bc85b114"));

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Proxy-Connection")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Expect")]
    public void Transport_headers_cannot_be_overridden(string name)
    {
        Assert.Contains(Validate(new HttpHeader(name, "value")).Errors, e => e.Code == ConfigurationErrorCode.HeaderNotAllowed);
    }

    [Theory]
    [InlineData("X-Valid", "ok")]
    [InlineData("Authorization", "Bearer synthetic-test-token")]
    [InlineData("Content-Type", "application/json")]
    [InlineData("X-Empty", "")]
    [InlineData("X-Tab", "one\ttwo")]
    public void End_to_end_headers_are_preserved(string name, string value)
    {
        var result = Validate(new HttpHeader(name, value));
        Assert.True(result.IsValid);
        Assert.Equal(new HttpHeader(name, value), Assert.Single(result.Plan!.Headers));
    }

    [Theory]
    [InlineData(null, "ok")]
    [InlineData("X-Test", null)]
    [InlineData("", "ok")]
    [InlineData("Bad Name", "ok")]
    [InlineData("X:Name", "ok")]
    [InlineData("X\r\nInjected", "ok")]
    [InlineData("X-Test", "ok\r\nInjected: bad")]
    [InlineData("X-Test", "nul\0")]
    [InlineData("X-Test", "del\u007f")]
    [InlineData("X-Test", "não ASCII")]
    public void Invalid_header_syntax_is_rejected(string? name, string? value)
    {
        var result = Validate(new HttpHeader(name, value));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == ConfigurationErrorCode.InvalidHeader && e.HeaderIndex == 0);
    }

    [Fact]
    public void Null_header_entry_is_rejected_without_throwing()
    {
        Assert.False(Validate([null!]).IsValid);
    }

    [Fact]
    public void Duplicate_header_names_are_case_insensitive()
    {
        var result = Validate(new("X-Key", "a"), new("x-key", "b"));
        Assert.Contains(result.Errors, e => e.Code == ConfigurationErrorCode.DuplicateHeader && e.HeaderIndex == 1);
    }

    [Fact]
    public void Header_budget_includes_separators_and_final_crlf()
    {
        // Uma linha: "X: " + valor + CRLF, seguida de CRLF final = 7 bytes fora do valor.
        string value = new('a', 16_384 - 7);
        Assert.True(Validate(new HttpHeader("X", value)).IsValid);
        Assert.False(Validate(new HttpHeader("X", value + "a")).IsValid);
        Assert.False(Validate(new("X", value), new("Y", "b")).IsValid);
    }

    [Fact]
    public void Header_count_bounds_validation_work_and_error_volume()
    {
        var atLimit = Enumerable.Range(0, 128).Select(i => new HttpHeader($"X-{i}", "a")).ToArray();
        Assert.True(Validate(atLimit).IsValid);
        var overLimit = atLimit.Append(new HttpHeader("X-129", "a")).ToArray();
        Assert.Equal(ConfigurationErrorCode.HeadersTooLarge, Assert.Single(Validate(overLimit).Errors).Code);
    }

    [Fact]
    public void Accepted_plan_does_not_share_mutable_input_buffers()
    {
        byte[] bytes = [1, 2, 3];
        HttpHeader[] headers = [new("Content-Type", "application/octet-stream")];
        var config = new LoadTestConfiguration { Url = "http://127.0.0.1/", Method = "POST", Headers = headers, Payload = bytes };
        var plan = LocalPlanValidator.Validate(config, Guid.NewGuid()).Plan!;
        bytes[0] = 99;
        headers[0] = new("X-Replaced", "changed");
        var changedPayload = plan.Payload.SetItem(0, 42);
        var changedHeaders = plan.Headers.SetItem(0, new("X-Replaced", "changed"));
        Assert.Equal(new byte[] { 1, 2, 3 }, plan.Payload.ToArray());
        Assert.Equal("Content-Type", plan.Headers[0].Name);
        Assert.Equal(42, changedPayload[0]);
        Assert.Equal("X-Replaced", changedHeaders[0].Name);
    }
}
