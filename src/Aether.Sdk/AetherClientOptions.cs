using System;

namespace Aether.Sdk;

/// <summary>
/// Configuration options for <see cref="AetherClient"/>.
/// Configuration is resolved in priority order:
///   BaseUrl: explicit value > AETHER_BASE_URL env var > "https://api.aetherdb.ai"
///   ApiKey:  explicit value > AETHER_API_KEY env var > null
/// </summary>
public class AetherClientOptions
{
    /// <summary>Base URL of the Aether API. Default: https://api.aetherdb.ai</summary>
    public string BaseUrl { get; set; } =
        Environment.GetEnvironmentVariable("AETHER_BASE_URL") is { Length: > 0 } envUrl
            ? envUrl
            : "https://api.aetherdb.ai";

    /// <summary>API key for authentication. Sent as a Bearer token.</summary>
    public string? ApiKey { get; set; } =
        Environment.GetEnvironmentVariable("AETHER_API_KEY") is { Length: > 0 } envKey
            ? envKey
            : null;

    /// <summary>Request timeout. Default: 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of retries on transient failures (429, 502, 503, 504, network errors). Default: 2.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base delay for exponential backoff between retries. Default: 0.5 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(0.5);
}
