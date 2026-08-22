using FactorioManager.Api;
using Xunit;

namespace FactorioManager.Api.Tests;

public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("token=redaction-sentinel", "redaction-sentinel")]
    [InlineData("password:redaction-sentinel", "redaction-sentinel")]
    [InlineData("https://factorio.invalid/download?username=user&token=redaction-sentinel", "redaction-sentinel")]
    [InlineData("setup code=redaction-sentinel", "redaction-sentinel")]
    [InlineData("Authorization: Bearer redaction-sentinel", "redaction-sentinel")]
    [InlineData("authorization=redaction-sentinel", "redaction-sentinel")]
    [InlineData("https://factorio.invalid/download?authorization=redaction-sentinel&next=1", "redaction-sentinel")]
    public void RedactionRemovesCredentialValues(string input, string secret)
    {
        var safe = SafeDiagnostics.Redact(input);

        Assert.DoesNotContain(secret, safe, StringComparison.Ordinal);
        Assert.Contains("[redacted]", safe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://factorio.invalid/setup?setup-code=redaction-sentinel&next=1")]
    [InlineData("https://factorio.invalid/setup?setup_code=redaction-sentinel&next=1")]
    [InlineData("https://factorio.invalid/setup?setupCode=redaction-sentinel&next=1")]
    public void RedactionRemovesSetupCodeFromUrlQueries(string input)
    {
        var safe = SafeDiagnostics.Redact(input);

        Assert.DoesNotContain("redaction-sentinel", safe, StringComparison.Ordinal);
        Assert.Contains("[redacted]", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionBoundsDiagnostics()
    {
        var safe = SafeDiagnostics.Redact(new string('x', 5000));

        Assert.Equal(4001, safe.Length);
        Assert.EndsWith("…", safe);
    }

    [Fact]
    public void ErrorContractCarriesStableFieldsWithoutSensitiveDetail()
    {
        var response = new ApiErrorResponse("Save creation failed", 400, SafeDiagnostics.Redact("token=redaction-sentinel; retry"), "request-123");

        Assert.Equal(400, response.Status);
        Assert.Equal("request-123", response.RequestId);
        Assert.Equal("token=[redacted]; retry", response.Detail);
        Assert.DoesNotContain("redaction-sentinel", response.Detail, StringComparison.Ordinal);
    }
}
