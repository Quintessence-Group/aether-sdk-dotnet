using System.Net;
using Xunit;

namespace Aether.Sdk.Tests;

public class ExceptionTests
{
    [Fact]
    public void AetherException_IsException()
    {
        var ex = new AetherException("test");
        Assert.IsAssignableFrom<Exception>(ex);
        Assert.Equal("test", ex.Message);
    }

    [Fact]
    public void AetherApiException_ExtendsAetherException()
    {
        var ex = new AetherApiException(HttpStatusCode.NotFound, "Document not found");
        Assert.IsAssignableFrom<AetherException>(ex);
        Assert.IsAssignableFrom<Exception>(ex);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Document not found", ex.Body);
        Assert.Equal("Document not found", ex.Message);
    }

    [Fact]
    public void AetherApiException_CanBeCaughtAsAetherException()
    {
        var ex = new AetherApiException(HttpStatusCode.Unauthorized, "Invalid API key");
        try
        {
            throw ex;
        }
        catch (AetherException caught)
        {
            Assert.IsType<AetherApiException>(caught);
        }
    }

    // Phase 8 / ADR-015 — typed-exception factory.
    [Fact]
    public void FromResponse_ReturnsCreditExhaustedFor402CreditExhausted()
    {
        var ex = AetherApiException.FromResponse(
            (HttpStatusCode)402, "Top up your balance", "credit_exhausted");
        Assert.IsType<CreditExhaustedException>(ex);
        Assert.IsAssignableFrom<AetherApiException>(ex);
        Assert.Equal((HttpStatusCode)402, ex.StatusCode);
        Assert.Equal("credit_exhausted", ex.ErrorCode);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public void FromResponse_ReturnsFreeLimitExceededFor402FreeLimitExceeded()
    {
        var ex = AetherApiException.FromResponse(
            (HttpStatusCode)402, "Free plan limit", "free_limit_exceeded");
        Assert.IsType<FreeLimitExceededException>(ex);
        // Distinct from CreditExhaustedException — siblings, not subclasses.
        Assert.IsNotType<CreditExhaustedException>(ex);
    }

    [Fact]
    public void FromResponse_ReturnsTenantPausedFor403TenantPaused()
    {
        var ex = AetherApiException.FromResponse(
            (HttpStatusCode)403, "Tenant paused by operator", "tenant_paused");
        Assert.IsType<TenantPausedException>(ex);
        Assert.Equal((HttpStatusCode)403, ex.StatusCode);
    }

    [Fact]
    public void FromResponse_FallsBackForUnknown402Code()
    {
        var ex = AetherApiException.FromResponse(
            (HttpStatusCode)402, "Other billing", "something_else");
        Assert.Equal(typeof(AetherApiException), ex.GetType());
    }

    [Fact]
    public void FromResponse_FallsBackForUnrelatedStatus()
    {
        var ex = AetherApiException.FromResponse(
            HttpStatusCode.NotFound, "Document not found", null);
        Assert.Equal(typeof(AetherApiException), ex.GetType());
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(402, false)] // credit_exhausted — never retry
    [InlineData(403, false)] // tenant_paused — never retry
    [InlineData(404, false)]
    [InlineData(500, false)] // intentionally non-retryable
    public void IsRetryable_MatchesPlanClassification(int status, bool expected)
    {
        var ex = new AetherApiException((HttpStatusCode)status, "x");
        Assert.Equal(expected, ex.IsRetryable);
    }

    [Fact]
    public void AetherNetworkException_ExtendsAetherException()
    {
        var inner = new HttpRequestException("Connection refused");
        var ex = new AetherNetworkException("Failed to connect", inner);
        Assert.IsAssignableFrom<AetherException>(ex);
        Assert.Equal("Failed to connect", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
