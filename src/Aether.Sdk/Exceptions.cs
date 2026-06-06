using System.Net;

namespace Aether.Sdk;

/// <summary>
/// Base exception for all Aether SDK errors.
/// </summary>
public class AetherException : Exception
{
    public AetherException(string message) : base(message) { }
    public AetherException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when the Aether API returns a non-2xx HTTP response.
/// </summary>
public class AetherApiException : AetherException
{
    /// <summary>HTTP status code returned by the server.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Error body returned by the server.</summary>
    public string Body { get; }

    /// <summary>Machine-readable error code returned by the server, if any.</summary>
    public string? ErrorCode { get; }

    public AetherApiException(HttpStatusCode statusCode, string body, string? errorCode = null)
        : base(body)
    {
        StatusCode = statusCode;
        Body = body;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Whether this error is likely transient and worth retrying. The SDK's
    /// internal retry loop already honours this — application code branching
    /// on a thrown exception can use it to decide whether to surface the
    /// failure or queue the operation.
    /// </summary>
    public bool IsRetryable =>
        (int)StatusCode is 429 or 502 or 503 or 504;

    /// <summary>
    /// Build the most-specific <see cref="AetherApiException"/> subclass for
    /// the given response. Inspects the structured <c>code</c> field
    /// (Phase 8 / ADR-015 wire shape); unknown codes fall back to the base
    /// <see cref="AetherApiException"/>.
    /// </summary>
    public static AetherApiException FromResponse(HttpStatusCode statusCode, string body, string? errorCode)
    {
        var status = (int)statusCode;
        return (status, errorCode) switch
        {
            (402, "credit_exhausted") => new CreditExhaustedException(statusCode, body, errorCode),
            (402, "free_limit_exceeded") => new FreeLimitExceededException(statusCode, body, errorCode),
            (403, "tenant_paused") => new TenantPausedException(statusCode, body, errorCode),
            _ => new AetherApiException(statusCode, body, errorCode),
        };
    }
}

/// <summary>
/// Thrown when a paid tenant's prepaid credit balance is exhausted (HTTP 402,
/// <c>code = "credit_exhausted"</c>). Top up via the Portal billing page; the
/// SDK never retries — the operation is permanently denied until credit is
/// added.
/// </summary>
public class CreditExhaustedException : AetherApiException
{
    public CreditExhaustedException(HttpStatusCode statusCode, string body, string? errorCode = null)
        : base(statusCode, body, errorCode) { }
}

/// <summary>
/// Thrown when a Free-tier tenant exceeds a hard plan limit (HTTP 402,
/// <c>code = "free_limit_exceeded"</c>). Distinct from
/// <see cref="CreditExhaustedException"/> so dashboards can separate abuse
/// signal from billing failures. Resolution is a plan upgrade, not a top-up.
/// </summary>
public class FreeLimitExceededException : AetherApiException
{
    public FreeLimitExceededException(HttpStatusCode statusCode, string body, string? errorCode = null)
        : base(statusCode, body, errorCode) { }
}

/// <summary>
/// Thrown when an operator has paused a tenant via the spike detector or
/// admin console (HTTP 403, <c>code = "tenant_paused"</c>). Not retryable;
/// the tenant must be un-paused out-of-band.
/// </summary>
public class TenantPausedException : AetherApiException
{
    public TenantPausedException(HttpStatusCode statusCode, string body, string? errorCode = null)
        : base(statusCode, body, errorCode) { }
}

/// <summary>
/// Thrown when a network-level failure prevents the request from completing.
/// </summary>
public class AetherNetworkException : AetherException
{
    public AetherNetworkException(string message, Exception innerException)
        : base(message, innerException) { }
}
