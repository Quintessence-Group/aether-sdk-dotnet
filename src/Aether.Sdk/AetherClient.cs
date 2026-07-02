using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Aether.Sdk;

/// <summary>
/// Client for the Aether dRAG HTTP API.
/// </summary>
public class AetherClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;

    // When set, every partition-aware read and write is scoped to this partition.
    // Bound to the object by Partition(...); never read from a method parameter.
    // Null on the default (unscoped) client.
    private readonly string? _partition;

    private bool _disposed;

    /// <summary>SDK version, reported in the User-Agent header. Keep in sync with the csproj &lt;Version&gt;.</summary>
    private const string Version = "0.3.2";

    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        (HttpStatusCode)429,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Dictionary<string, string> ExtensionMimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
    };

    private static string GuessContentType(string filename)
    {
        var ext = Path.GetExtension(filename);
        return ext != null && ExtensionMimeMap.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }

    // Explicit extension → content type map for batch ingestion. Kept
    // separate from ExtensionMimeMap so common document types resolve the same way
    // on every OS: `.markdown`/`.text` are spelled out here, and anything unlisted
    // falls back to application/octet-stream at the call site. Mirrors the Python
    // SDK's INGEST_CONTENT_TYPES.
    private static readonly Dictionary<string, string> IngestContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".txt"] = "text/plain",
        [".text"] = "text/plain",
        [".pdf"] = "application/pdf",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".html"] = "text/html",
        [".htm"] = "text/html",
    };

    /// <summary>Resolve a file path's content type from its extension for batch
    /// ingestion: the explicit ingest map first, then <c>application/octet-stream</c>
    /// for anything unlisted.</summary>
    internal static string ResolveIngestContentType(string path)
    {
        var ext = Path.GetExtension(path);
        return ext != null && IngestContentTypes.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }

    private static string BuildQueryString(
        string baseQuery,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        bool extractFacts = false)
    {
        var qs = baseQuery;
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        if (chunking?.ChunkSize > 0)
            qs += $"&chunk_size={chunking.ChunkSize}";
        if (chunking?.Overlap > 0)
            qs += $"&overlap={chunking.Overlap}";
        if (!string.IsNullOrEmpty(entityId))
            qs += $"&entity_id={Uri.EscapeDataString(entityId)}";
        if (!string.IsNullOrEmpty(source))
            qs += $"&source={Uri.EscapeDataString(source)}";
        if (metadata is { Count: > 0 })
            qs += $"&metadata={Uri.EscapeDataString(JsonSerializer.Serialize(metadata, JsonOptions))}";
        if (extractFacts)
            qs += "&extract_facts=true";
        return qs;
    }

    private static string AppendSearchFilters(
        string baseQuery,
        string? entityId,
        string? since,
        string? until,
        int? lastNDays,
        float? maxDistance = null,
        double? recencyWeight = null,
        double? halfLifeDays = null,
        double? freshnessWeight = null,
        double? freshnessHalfLifeDays = null,
        IReadOnlyDictionary<string, object?>? filter = null)
    {
        var qs = baseQuery;
        if (!string.IsNullOrEmpty(entityId))
            qs += $"&entity_id={Uri.EscapeDataString(entityId)}";
        if (!string.IsNullOrEmpty(since))
            qs += $"&since={Uri.EscapeDataString(since)}";
        if (!string.IsNullOrEmpty(until))
            qs += $"&until={Uri.EscapeDataString(until)}";
        if (lastNDays.HasValue)
            qs += $"&last_n_days={lastNDays.Value}";
        if (maxDistance.HasValue)
            qs += $"&max_distance={maxDistance.Value.ToString(CultureInfo.InvariantCulture)}";
        if (recencyWeight.HasValue)
            qs += $"&recency_weight={recencyWeight.Value.ToString(CultureInfo.InvariantCulture)}";
        if (halfLifeDays.HasValue)
            qs += $"&half_life_days={halfLifeDays.Value.ToString(CultureInfo.InvariantCulture)}";
        if (freshnessWeight.HasValue)
            qs += $"&freshness_weight={freshnessWeight.Value.ToString(CultureInfo.InvariantCulture)}";
        if (freshnessHalfLifeDays.HasValue)
            qs += $"&freshness_half_life_days={freshnessHalfLifeDays.Value.ToString(CultureInfo.InvariantCulture)}";
        if (filter is { Count: > 0 })
            qs += $"&filter={Uri.EscapeDataString(JsonSerializer.Serialize(filter, JsonOptions))}";
        return qs;
    }

    // Appends the OR-list metadata filters (anyTags / contentTypes / sources) as
    // comma-joined query params (any_tags / content_type / source), the same CSV
    // convention as tags. Each is omitted when null or empty.
    private static string AppendMetadataFilters(
        string baseQuery,
        IReadOnlyList<string>? anyTags,
        IReadOnlyList<string>? contentTypes,
        IReadOnlyList<string>? sources)
    {
        var qs = baseQuery;
        if (anyTags is { Count: > 0 })
            qs += $"&any_tags={Uri.EscapeDataString(string.Join(",", anyTags))}";
        if (contentTypes is { Count: > 0 })
            qs += $"&content_type={Uri.EscapeDataString(string.Join(",", contentTypes))}";
        if (sources is { Count: > 0 })
            qs += $"&source={Uri.EscapeDataString(string.Join(",", sources))}";
        return qs;
    }

    /// <summary>
    /// Creates a new Aether client.
    /// </summary>
    public AetherClient(AetherClientOptions? options = null)
    {
        options ??= new AetherClientOptions();
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _maxRetries = Math.Max(0, options.MaxRetries);
        _retryBaseDelay = options.RetryBaseDelay;

        EnforceSecureBaseUrl(_baseUrl, options.ApiKey);

        _http = new HttpClient { Timeout = options.Timeout };
        // The public ctor builds and therefore owns the HttpClient; Dispose() closes it.
        _ownsHttpClient = true;
        ConfigureUserAgent(_http);

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>Adds the SDK User-Agent so the server can attribute traffic by SDK + version.</summary>
    private static void ConfigureUserAgent(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("aether-sdk-dotnet", Version));
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue($"({RuntimeInformation.FrameworkDescription})"));
    }

    /// <summary>
    /// Throws if an API key would be sent over cleartext HTTP to a non-loopback
    /// host. Loopback addresses (localhost, 127.0.0.0/8, ::1) are allowed so
    /// local development against a non-TLS node still works.
    /// </summary>
    private static void EnforceSecureBaseUrl(string baseUrl, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new AetherException(
                $"Refusing to send API key over insecure HTTP to '{uri.Host}'. " +
                "Use an https:// base URL, or omit the API key for local non-TLS endpoints.");
        }
    }

    /// <summary>
    /// Creates a new Aether client with an externally managed HttpClient.
    /// Useful for testing and dependency injection.
    /// </summary>
    internal AetherClient(HttpClient httpClient, string baseUrl)
    {
        _http = httpClient;
        // An externally supplied HttpClient is owned by the caller: Dispose() must
        // not close it.
        _ownsHttpClient = false;
        _baseUrl = baseUrl.TrimEnd('/');
        _maxRetries = 2;
        _retryBaseDelay = TimeSpan.FromSeconds(0.5);
        ConfigureUserAgent(_http);
    }

    /// <summary>
    /// Creates a partition-scoped clone that shares the parent's HttpClient and all
    /// connection config. The clone never owns the shared transport, so disposing it
    /// is a no-op for the HttpClient — the base client still owns and closes it.
    /// </summary>
    private AetherClient(AetherClient parent, string partition)
    {
        _http = parent._http;
        _ownsHttpClient = false; // never own a shared transport (mirrors Memory ownsClient:false)
        _baseUrl = parent._baseUrl;
        _maxRetries = parent._maxRetries;
        _retryBaseDelay = parent._retryBaseDelay;
        _partition = partition;
    }

    // ── Partition scoping ─────────────────────────────────────────────

    /// <summary>
    /// Returns a partition-scoped clone of this client. Every partition-aware read and
    /// write made through the returned handle is scoped to <paramref name="partitionId"/>;
    /// a multi-tenant key requires it. The scope is bound to the returned object — there
    /// is no per-call partition argument, so it cannot be forgotten, and reaching another
    /// partition requires obtaining a separate handle (e.g. <c>client.Partition("b")</c>).
    /// </summary>
    /// <remarks>
    /// The clone shares this client's HttpClient and all connection config (base URL,
    /// auth header, timeout, retries, backoff) and does not own that transport: disposing
    /// the clone leaves the parent's HttpClient open. Re-scoping is last-wins:
    /// <c>client.Partition("a").Partition("b")</c> is scoped to "b". Under a single-tenant
    /// key the top-level (unscoped) client sends no partition and operates on the default
    /// partition, so hello-world stays frictionless.
    /// </remarks>
    /// <param name="partitionId">The partition to scope to. Non-empty, non-whitespace, 1–256 characters.</param>
    /// <exception cref="ArgumentException">The partition ID is empty/whitespace or longer than 256 characters.</exception>
    public AetherClient Partition(string partitionId)
    {
        ValidatePartitionId(partitionId);
        return new AetherClient(this, partitionId);
    }

    private static void ValidatePartitionId(string partitionId)
    {
        if (string.IsNullOrWhiteSpace(partitionId))
            throw new ArgumentException(
                "partitionId cannot be empty and must contain a non-whitespace character", nameof(partitionId));
        if (partitionId.Length > 256)
            throw new ArgumentException(
                "partitionId cannot be longer than 256 characters", nameof(partitionId));
    }

    // Appends &partition=<id> to a query string when this client is partition-scoped,
    // URL-encoding exactly like entity_id. No-op on the unscoped client.
    private string AppendPartition(string query)
    {
        if (!string.IsNullOrEmpty(_partition))
            query += $"&partition={Uri.EscapeDataString(_partition!)}";
        return query;
    }

    // ── Memory graph transport hook ───────────────────────────────────

    /// <summary>
    /// Internal transport hook for the <see cref="Memory"/> graph facade. Reuses
    /// this client's URL building, partition scoping, retries, error mapping, and
    /// timeouts unchanged — the public raw-client surface is not extended.
    /// </summary>
    /// <remarks>
    /// <paramref name="query"/> carries the already-assembled filter pairs (no
    /// leading <c>&amp;</c> / <c>?</c>; e.g. <c>"entity_id=owner&amp;limit=10"</c>).
    /// This appends the partition (when scoped) and serializes <paramref name="body"/>
    /// with the same JSON options as every other call. A <c>Dictionary&lt;string,
    /// object?&gt;</c> body serializes a null value as an explicit JSON <c>null</c>
    /// (not dropped), which the fact endpoint relies on.
    /// </remarks>
    internal Task<T> SendMemoryAsync<T>(
        HttpMethod method,
        string path,
        string query,
        object? body,
        CancellationToken cancellationToken)
    {
        var finalQuery = AppendPartition(query);
        HttpContent? content = null;
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var fullPath = string.IsNullOrEmpty(finalQuery) ? path : $"{path}?{finalQuery}";
        return RequestAsync<T>(fullPath, method, content, cancellationToken);
    }

    // ── Internal helpers ──────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        int maxAttempts = _maxRetries + 1;

        for (int attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts - 1)
            {
                await DelayBeforeRetryAsync(attempt, retryAfter: null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode || attempt >= maxAttempts - 1)
                return response;

            if (!RetryableStatusCodes.Contains(response.StatusCode))
                return response;

            // Parse Retry-After header for 429 responses
            TimeSpan? retryAfter = null;
            if (response.StatusCode == (HttpStatusCode)429 &&
                response.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta.HasValue)
                    retryAfter = ra.Delta.Value;
                else if (ra.Date.HasValue)
                    retryAfter = ra.Date.Value - DateTimeOffset.UtcNow;
            }

            response.Dispose();
            await DelayBeforeRetryAsync(attempt, retryAfter, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayBeforeRetryAsync(
        int attempt,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        TimeSpan delay;

        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            delay = retryAfter.Value;
        }
        else
        {
            // Exponential backoff: baseDelay * 2^attempt
            delay = TimeSpan.FromTicks((long)(_retryBaseDelay.Ticks * Math.Pow(2, attempt)));
        }

        // Add jitter: random 0-50% of delay
#if NET8_0_OR_GREATER
        var jitter = TimeSpan.FromTicks((long)(delay.Ticks * Random.Shared.NextDouble() * 0.5));
#else
        var jitter = TimeSpan.FromTicks((long)(delay.Ticks * new Random().NextDouble() * 0.5));
#endif
        delay += jitter;

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Canonical public API version prefix. Every data route (documents, search,
    /// memory, partitions, archive) is served under this prefix. The public probe
    /// route <c>GET /status</c> is intentionally unversioned.
    /// </summary>
    private const string ApiVersionPrefix = "/v1";

    /// <summary>
    /// Prefixes a relative request path with the public API version. The prefix
    /// always goes before the path itself, never into the query string.
    /// Unversioned probe routes (<c>/status</c>) pass through untouched.
    /// </summary>
    private static string VersionedPath(string path)
    {
        var queryStart = path.IndexOf('?');
        var bare = queryStart < 0 ? path : path.Substring(0, queryStart);
        return bare == "/status" ? path : ApiVersionPrefix + path;
    }

    private async Task<T> RequestAsync<T>(
        string path,
        HttpMethod method,
        HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        // Data routes are rewritten under the /v1 API version prefix here, at the
        // transport boundary, so every caller (including the Memory facade)
        // versions its paths in one place.
        var url = $"{_baseUrl}{VersionedPath(path)}";

        // Capture content bytes up front so the factory can recreate the request
        // on each (re)try. Also capture the original Content-Type: ByteArrayContent
        // defaults to no Content-Type, so without this the server rejects the
        // rebuilt body with 415 Unsupported Media Type on every attempt.
        byte[]? contentBytes = null;
        MediaTypeHeaderValue? contentType = null;
        if (content != null)
        {
            contentBytes = await content.ReadAsByteArrayAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);
            contentType = content.Headers.ContentType;
        }

        // Mint one idempotency key per logical write, reused across retries so
        // the server can deduplicate a request whose response was lost in transit.
        string? idempotencyKey = method == HttpMethod.Post ? Guid.NewGuid().ToString() : null;

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(() =>
            {
                var msg = new HttpRequestMessage(method, url);
                if (contentBytes != null)
                {
                    var rebuilt = new ByteArrayContent(contentBytes);
                    // Preserve the original Content-Type on every attempt so JSON
                    // POST bodies keep their `application/json` media type.
                    if (contentType != null)
                        rebuilt.Headers.ContentType = contentType;
                    msg.Content = rebuilt;
                }
                if (idempotencyKey != null)
                    msg.Headers.Add("Idempotency-Key", idempotencyKey);
                return msg;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AetherNetworkException(
                $"Request timed out: {method} {url}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AetherNetworkException(
                $"Failed to connect to {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);

            string errorMessage;
            string? errorCode = null;
            try
            {
                var errorObj = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
                errorMessage = errorObj?.Error ?? response.ReasonPhrase ?? "Unknown error";
                errorCode = errorObj?.Code;
            }
            catch
            {
                errorMessage = response.ReasonPhrase ?? "Unknown error";
            }

            throw AetherApiException.FromResponse(response.StatusCode, errorMessage, errorCode);
        }

        var json = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
            cancellationToken
#endif
        ).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new AetherException($"Failed to deserialize response from {path}");
    }

    private async Task RequestVoidAsync(
        string path,
        HttpMethod method,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}{VersionedPath(path)}";

        string? idempotencyKey = method == HttpMethod.Post ? Guid.NewGuid().ToString() : null;

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(
                () =>
                {
                    var msg = new HttpRequestMessage(method, url);
                    if (idempotencyKey != null)
                        msg.Headers.Add("Idempotency-Key", idempotencyKey);
                    return msg;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AetherNetworkException(
                $"Request timed out: {method} {url}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AetherNetworkException(
                $"Failed to connect to {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);

            string errorMessage;
            string? errorCode = null;
            try
            {
                var errorObj = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
                errorMessage = errorObj?.Error ?? response.ReasonPhrase ?? "Unknown error";
                errorCode = errorObj?.Code;
            }
            catch
            {
                errorMessage = response.ReasonPhrase ?? "Unknown error";
            }

            throw AetherApiException.FromResponse(response.StatusCode, errorMessage, errorCode);
        }
    }

    private async Task<byte[]> RequestRawAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}{VersionedPath(path)}";

        HttpResponseMessage response;
        try
        {
            response = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AetherNetworkException(
                $"Request timed out: GET {url}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AetherNetworkException(
                $"Failed to connect to {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);

            string errorMessage;
            string? errorCode = null;
            try
            {
                var errorObj = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
                errorMessage = errorObj?.Error ?? response.ReasonPhrase ?? "Unknown error";
                errorCode = errorObj?.Code;
            }
            catch
            {
                errorMessage = response.ReasonPhrase ?? "Unknown error";
            }

            throw AetherApiException.FromResponse(response.StatusCode, errorMessage, errorCode);
        }

        return await response.Content.ReadAsByteArrayAsync(
#if NET8_0_OR_GREATER
            cancellationToken
#endif
        ).ConfigureAwait(false);
    }

    // ── Documents ─────────────────────────────────────────────────────

    /// <summary>Insert a document from raw bytes.
    /// If <paramref name="contentType"/> is null the type is guessed from the filename extension.</summary>
    /// <param name="entityId">Optional entity ID to associate with the document, for entity-scoped search and list filters.</param>
    /// <param name="source">Optional source label to associate with the document (e.g. its origin system), for source-scoped search and list filters.</param>
    public async Task<DocumentRecord> InsertAsync(
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        bool extractFacts = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("filename cannot be empty", nameof(filename));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        contentType ??= GuessContentType(filename);
        var query = AppendPartition(BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking, entityId, source, metadata, extractFacts));
        var content = new ByteArrayContent(data);
        return await RequestAsync<DocumentRecord>(
            $"/documents?{query}", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Insert raw text content.</summary>
    /// <param name="entityId">Optional entity ID to associate with the document, for entity-scoped search and list filters.</param>
    /// <param name="source">Optional source label to associate with the document (e.g. its origin system), for source-scoped search and list filters.</param>
    /// <param name="extractFacts">When true, distill the text into atomic facts server-side, each stored as a sibling document tagged <c>kind:fact</c> and linked to this document. Requires fact extraction to be configured on the node.</param>
    public async Task<DocumentRecord> InsertTextAsync(
        string text,
        string filename = "text.txt",
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        bool extractFacts = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("text cannot be empty", nameof(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        return await InsertAsync(bytes, filename, "text/plain", tags, chunking, entityId, source, metadata, extractFacts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Insert a document from a Stream without buffering the entire body in memory.
    /// Unlike <see cref="InsertAsync"/>, this method does not retry on transient errors
    /// because the stream may not be re-readable.</summary>
    /// <param name="stream">The document content as a readable stream.</param>
    /// <param name="filename">Filename for the document. Default: "upload.bin".</param>
    /// <param name="contentType">MIME type. Default: "application/octet-stream".</param>
    /// <param name="tags">Optional tags to associate with the document.</param>
    /// <param name="entityId">Optional entity ID to associate with the document, for entity-scoped search and list filters.</param>
    /// <param name="source">Optional source label to associate with the document (e.g. its origin system), for source-scoped search and list filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DocumentRecord> InsertStreamAsync(
        Stream stream,
        string filename = "upload.bin",
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        contentType ??= "application/octet-stream";
        var query = AppendPartition(BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, entityId: entityId, source: source, metadata: metadata));
        var url = $"{_baseUrl}{VersionedPath("/documents")}?{query}";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StreamContent(stream),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AetherNetworkException(
                $"Request timed out: POST {url}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AetherNetworkException(
                $"Failed to connect to {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
                cancellationToken
#endif
            ).ConfigureAwait(false);

            string errorMessage;
            string? errorCode = null;
            try
            {
                var errorObj = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOptions);
                errorMessage = errorObj?.Error ?? response.ReasonPhrase ?? "Unknown error";
                errorCode = errorObj?.Code;
            }
            catch
            {
                errorMessage = response.ReasonPhrase ?? "Unknown error";
            }

            throw AetherApiException.FromResponse(response.StatusCode, errorMessage, errorCode);
        }

        var json = await response.Content.ReadAsStringAsync(
#if NET8_0_OR_GREATER
            cancellationToken
#endif
        ).ConfigureAwait(false);

        return JsonSerializer.Deserialize<DocumentRecord>(json, JsonOptions)
            ?? throw new AetherException("Failed to deserialize response from /documents");
    }

    /// <summary>Update an existing document.
    /// If <paramref name="contentType"/> is null the type is guessed from the filename extension.</summary>
    /// <param name="entityId">New entity ID for the document. The update replaces the stored entity ID;
    /// when omitted, any existing entity ID is cleared (mirrors tags semantics).</param>
    /// <param name="source">New source label for the document. The update replaces the stored source;
    /// when omitted, any existing source is cleared (mirrors entity ID semantics).</param>
    public async Task<DocumentRecord> UpdateAsync(
        string docId,
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("filename cannot be empty", nameof(filename));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        contentType ??= GuessContentType(filename);
        var query = AppendPartition(BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking, entityId, source, metadata));
        var content = new ByteArrayContent(data);
        return await RequestAsync<DocumentRecord>(
            $"/documents/{Uri.EscapeDataString(docId)}?{query}", HttpMethod.Put, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Get document metadata.</summary>
    public async Task<DocumentRecord> GetAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        return await RequestAsync<DocumentRecord>(
            $"/documents/{Uri.EscapeDataString(docId)}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Download a document as raw bytes.</summary>
    public async Task<byte[]> DownloadAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        return await RequestRawAsync(
            $"/documents/{Uri.EscapeDataString(docId)}/download", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>List active documents with pagination.</summary>
    /// <param name="offset">Number of documents to skip. Default: 0.</param>
    /// <param name="limit">Maximum documents to return. Default: 50, max: 1000.</param>
    /// <param name="entityId">Only list documents with this entity ID.</param>
    /// <param name="since">Only list documents created at or after this RFC 3339 timestamp (inclusive).</param>
    /// <param name="until">Only list documents created at or before this RFC 3339 timestamp (inclusive).</param>
    /// <param name="lastNDays">Only list documents created in the last N days (UTC, server clock).</param>
    /// <param name="tags">Only list documents carrying every one of these tags (AND).</param>
    /// <param name="anyTags">Only list documents carrying at least one of these tags (OR).</param>
    /// <param name="contentTypes">Only list documents whose content type is one of these (OR).</param>
    /// <param name="sources">Only list documents whose source is one of these (OR).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DocumentListResult> ListAsync(
        int offset = 0,
        int limit = 50,
        string? entityId = null,
        string? since = null,
        string? until = null,
        int? lastNDays = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? anyTags = null,
        IReadOnlyList<string>? contentTypes = null,
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        CancellationToken cancellationToken = default)
    {
        var qs = $"offset={offset}&limit={limit}";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        qs = AppendMetadataFilters(qs, anyTags, contentTypes, sources);
        qs = AppendPartition(AppendSearchFilters(qs, entityId, since, until, lastNDays, filter: filter));
        var response = await RequestAsync<DocumentListResponse>(
            $"/documents?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new DocumentListResult
        {
            Documents = response.Documents,
            Total = response.Total,
            HasMore = response.HasMore,
        };
    }

    /// <summary>Tombstone a document.</summary>
    public async Task DeleteAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        await RequestVoidAsync(
            $"/documents/{Uri.EscapeDataString(docId)}", HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently purge a document: it is removed from the primary
    /// store and both the vector and keyword indexes, and its encryption key is
    /// shredded. This is <b>irreversible</b> — nothing is recoverable afterwards
    /// (the right-to-be-forgotten path). Use <see cref="DeleteAsync"/> for a
    /// recoverable tombstone.
    /// </summary>
    public async Task HardDeleteAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        await RequestVoidAsync(
            $"/documents/{Uri.EscapeDataString(docId)}?hard=true", HttpMethod.Delete, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restore a tombstoned document.</summary>
    public async Task RestoreAsync(
        string docId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        await RequestVoidAsync(
            $"/documents/{Uri.EscapeDataString(docId)}/restore", HttpMethod.Post, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Backfill <c>entity_id</c> on the tenant's existing documents from a tag convention.
    /// For every active document, a tag starting with <paramref name="tagPrefix"/> (e.g. <c>"patient:"</c>)
    /// sets the entity ID to the suffix after the prefix when exactly one such tag exists; documents with
    /// ambiguous (two or more) or absent matches are skipped. Documents that already have an entity ID are
    /// left alone unless <paramref name="overwrite"/> is true. This is a metadata-only operation — documents
    /// are not re-embedded.</summary>
    /// <param name="tagPrefix">Tag prefix to match (required, non-empty); the suffix after it becomes the entity ID.</param>
    /// <param name="overwrite">When true, also overwrite documents that already have an entity ID. Default: false.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A report tallying scanned, updated, and skipped documents.</returns>
    public async Task<EntityBackfillReport> BackfillEntityFromTagsAsync(
        string tagPrefix,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tagPrefix))
            throw new ArgumentException("tagPrefix cannot be empty", nameof(tagPrefix));
        var body = new EntityBackfillRequest { TagPrefix = tagPrefix, Overwrite = overwrite };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await RequestAsync<EntityBackfillReport>(
            "/documents/backfill-entity", HttpMethod.Post, content, ct).ConfigureAwait(false);
    }

    // ── Ingestion ─────────────────────────────────────────────────────

    /// <summary>Ingest many files in one call.</summary>
    /// <remarks>
    /// Each file is read from disk and inserted independently via <see cref="InsertAsync"/>;
    /// the content type is resolved from the extension (<c>.md</c> / <c>.txt</c> / <c>.pdf</c>
    /// and friends — see the ingest content-type map). Chunking uses the server defaults
    /// unless <paramref name="chunking"/> is given.
    /// <para>
    /// A file the engine cannot ingest — an unsupported or binary type, one that needs the
    /// server-side document parser when it is not configured, or a file over the size limit
    /// (HTTP 413 / 415 / 422) — is <b>reported</b> in the returned results
    /// (<see cref="IngestResult.Status"/> = <c>"skipped"</c>) rather than aborting the batch
    /// or failing silently. A file that cannot be read off disk is reported as
    /// <c>"error"</c>. Set <paramref name="raiseOnError"/> to true to re-throw instead.
    /// </para>
    /// <para>Returns one <see cref="IngestResult"/> per input path, in order.</para>
    /// </remarks>
    /// <param name="paths">The file paths to ingest.</param>
    /// <param name="tags">Optional tags to associate with every ingested document.</param>
    /// <param name="chunking">Optional chunking config; server defaults when null.</param>
    /// <param name="entityId">Optional entity ID to associate with every ingested document.</param>
    /// <param name="source">Optional source label to associate with every ingested document.</param>
    /// <param name="raiseOnError">When true, re-throw the underlying exception instead of reporting
    /// a <c>"skipped"</c>/<c>"error"</c> result.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<IngestResult>> IngestFilesAsync(
        IEnumerable<string> paths,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        bool raiseOnError = false,
        CancellationToken ct = default)
    {
        if (paths is null)
            throw new ArgumentNullException(nameof(paths));

        var results = new List<IngestResult>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var contentType = ResolveIngestContentType(path);

            byte[] data;
            try
            {
#if NET8_0_OR_GREATER
                data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
#else
                data = File.ReadAllBytes(path);
#endif
            }
            catch (IOException ex)
            {
                if (raiseOnError)
                    throw;
                results.Add(new IngestResult
                {
                    Path = path,
                    Status = "error",
                    ContentType = contentType,
                    Error = ex.Message,
                });
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (raiseOnError)
                    throw;
                results.Add(new IngestResult
                {
                    Path = path,
                    Status = "error",
                    ContentType = contentType,
                    Error = ex.Message,
                });
                continue;
            }

            try
            {
                var filename = Path.GetFileName(path);
                var record = await InsertAsync(
                    data, filename, contentType, tags, chunking, entityId, source,
                    cancellationToken: ct).ConfigureAwait(false);
                results.Add(new IngestResult
                {
                    Path = path,
                    Status = "ingested",
                    DocId = record.DocId,
                    ContentType = contentType,
                });
            }
            catch (AetherApiException ex)
            {
                if (raiseOnError)
                    throw;
                // 413 (too large) / 415 (unsupported media) / 422 (unprocessable —
                // unknown or binary type the parser can't handle) are per-file
                // rejections the caller can't fix by retrying: report, don't abort.
                var status = (int)ex.StatusCode is 413 or 415 or 422 ? "skipped" : "error";
                results.Add(new IngestResult
                {
                    Path = path,
                    Status = status,
                    ContentType = contentType,
                    Error = ex.Message,
                });
            }
        }
        return results;
    }

    /// <summary>Ingest every file under <paramref name="directory"/>.</summary>
    /// <remarks>
    /// Walks <paramref name="directory"/> (recursively by default) and ingests each file via
    /// <see cref="IngestFilesAsync"/>. Pass <paramref name="extensions"/>
    /// (e.g. <c>[".md", ".txt", ".pdf"]</c>) to restrict which files are loaded; leading dots
    /// and case are optional. See <see cref="IngestFilesAsync"/> for how unsupported files are
    /// reported. Files are ingested in sorted (ordinal) path order.
    /// </remarks>
    /// <param name="directory">The directory to walk.</param>
    /// <param name="extensions">Optional extension allow-list; leading dots and case are optional.</param>
    /// <param name="recursive">Whether to descend into subdirectories. Default: true.</param>
    /// <param name="tags">Optional tags to associate with every ingested document.</param>
    /// <param name="chunking">Optional chunking config; server defaults when null.</param>
    /// <param name="entityId">Optional entity ID to associate with every ingested document.</param>
    /// <param name="source">Optional source label to associate with every ingested document.</param>
    /// <param name="raiseOnError">When true, re-throw instead of reporting a skipped/error result.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null/empty.</exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directory"/> is not a directory.</exception>
    public async Task<IReadOnlyList<IngestResult>> IngestDirectoryAsync(
        string directory,
        IEnumerable<string>? extensions = null,
        bool recursive = true,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        bool raiseOnError = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("directory cannot be empty", nameof(directory));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"not a directory: {directory}");

        HashSet<string>? allowed = null;
        if (extensions != null)
        {
            allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in extensions)
                allowed.Add(e.StartsWith(".", StringComparison.Ordinal) ? e : "." + e);
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(directory, "*", searchOption)
            .Where(p => allowed == null || allowed.Contains(Path.GetExtension(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return await IngestFilesAsync(
            files, tags, chunking, entityId, source, raiseOnError, ct).ConfigureAwait(false);
    }

    // ── Search ────────────────────────────────────────────────────────

    /// <summary>Similarity search across documents.</summary>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 10.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="entityId">Only match documents with this entity ID.</param>
    /// <param name="since">Only match documents created at or after this RFC 3339 timestamp (inclusive).</param>
    /// <param name="until">Only match documents created at or before this RFC 3339 timestamp (inclusive).</param>
    /// <param name="lastNDays">Only match documents created in the last N days (UTC, server clock).</param>
    /// <param name="maxDistance">Exclude results with a distance greater than this value.</param>
    /// <param name="anyTags">Only match documents carrying at least one of these tags (OR).</param>
    /// <param name="contentTypes">Only match documents whose content type is one of these (OR).</param>
    /// <param name="sources">Only match documents whose source is one of these (OR).</param>
    /// <param name="recencyWeight">Blend recency into ranking, in <c>[0, 1]</c>. 0 (default/null) = pure relevance; higher weights favor recently created documents, with the recency contribution decaying by the configured half-life.</param>
    /// <param name="halfLifeDays">Recency decay half-life in days (must be &gt; 0); the age at which the recency contribution halves. Only meaningful when <paramref name="recencyWeight"/> &gt; 0. Server default is 30 when omitted.</param>
    /// <param name="freshnessWeight">Blend freshness into ranking, in <c>[0, 1]</c>: boosts recently updated documents (<c>updated_at</c>, falling back to <c>created_at</c>). Composes with <paramref name="recencyWeight"/>; the server rejects a combined weight above 1. May require a Scale plan or higher.</param>
    /// <param name="freshnessHalfLifeDays">Freshness decay half-life in days (must be &gt; 0); the age at which the freshness contribution halves. Only meaningful when <paramref name="freshnessWeight"/> &gt; 0. Server default is 14 when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int k = 10,
        IReadOnlyList<string>? tags = null,
        string? entityId = null,
        string? since = null,
        string? until = null,
        int? lastNDays = null,
        float? maxDistance = null,
        IReadOnlyList<string>? anyTags = null,
        IReadOnlyList<string>? contentTypes = null,
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        double? recencyWeight = null,
        double? halfLifeDays = null,
        double? freshnessWeight = null,
        double? freshnessHalfLifeDays = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query cannot be empty", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var qs = $"q={Uri.EscapeDataString(query)}&k={k}";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        qs = AppendMetadataFilters(qs, anyTags, contentTypes, sources);
        qs = AppendPartition(AppendSearchFilters(qs, entityId, since, until, lastNDays, maxDistance, recencyWeight, halfLifeDays, freshnessWeight, freshnessHalfLifeDays, filter));
        var response = await RequestAsync<SearchResponse>(
            $"/search?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
        StampQueryId(response.Results, response.QueryId);
        return response.Results;
    }

    /// <summary>
    /// Copies the response-level usage-feedback <c>query_id</c> (present only when
    /// feedback capture is enabled for the tenant) onto every hit, so a caller can
    /// pass any hit's <see cref="SearchResult.QueryId"/> straight to
    /// <see cref="SendSearchFeedbackAsync"/>. A null <paramref name="queryId"/>
    /// (feedback capture disabled) leaves every hit's QueryId null — the tolerant
    /// path for servers that don't send the field.
    /// </summary>
    private static void StampQueryId(List<SearchResult> results, string? queryId)
    {
        if (string.IsNullOrEmpty(queryId))
            return;
        foreach (var r in results)
            r.QueryId = queryId;
    }

    /// <summary>Report how a search result was actually used, tying retrieval
    /// quality back to real outcomes.</summary>
    /// <remarks>
    /// Requires usage-feedback capture to be enabled for your tenant; search
    /// results then carry <see cref="SearchResult.QueryId"/> to pass here (null
    /// otherwise). The server rejects an unknown <paramref name="queryId"/> with
    /// 404 and an invalid <paramref name="signal"/> with 400 (both surface as
    /// <see cref="AetherApiException"/>).
    /// </remarks>
    /// <param name="queryId">The <see cref="SearchResult.QueryId"/> carried by the search results.</param>
    /// <param name="docId">The <see cref="SearchResult.DocId"/> of the hit the signal is about.</param>
    /// <param name="signal">One of <c>used</c> (the hit informed the answer), <c>cited</c>
    /// (quoted/referenced directly), or <c>ignored</c> (retrieved but unused).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SendSearchFeedbackAsync(
        string queryId,
        string docId,
        string signal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(queryId))
            throw new ArgumentException("queryId cannot be empty", nameof(queryId));
        if (string.IsNullOrEmpty(docId))
            throw new ArgumentException("docId cannot be empty", nameof(docId));
        if (string.IsNullOrEmpty(signal))
            throw new ArgumentException("signal cannot be empty", nameof(signal));
        var body = new SearchFeedbackRequest
        {
            QueryId = queryId,
            DocId = docId,
            Signal = signal,
        };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        await RequestAsync<SearchFeedbackResponse>(
            "/search/feedback", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Search with content retrieval. Results are deduplicated by DocId
    /// (highest-scoring match wins). Falls back to <see cref="DownloadAsync"/>
    /// for results missing inline content.</summary>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 5.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="entityId">Only match documents with this entity ID.</param>
    /// <param name="since">Only match documents created at or after this RFC 3339 timestamp (inclusive).</param>
    /// <param name="until">Only match documents created at or before this RFC 3339 timestamp (inclusive).</param>
    /// <param name="lastNDays">Only match documents created in the last N days (UTC, server clock).</param>
    /// <param name="maxDistance">Exclude results with a distance greater than this value.</param>
    /// <param name="anyTags">Only match documents carrying at least one of these tags (OR).</param>
    /// <param name="contentTypes">Only match documents whose content type is one of these (OR).</param>
    /// <param name="sources">Only match documents whose source is one of these (OR).</param>
    /// <param name="recencyWeight">Blend recency into ranking, in <c>[0, 1]</c>; forwarded to search. See <see cref="SearchAsync"/>.</param>
    /// <param name="halfLifeDays">Recency decay half-life in days (&gt; 0); forwarded to search. See <see cref="SearchAsync"/>.</param>
    /// <param name="freshnessWeight">Blend freshness (recent updates) into ranking, in <c>[0, 1]</c>; forwarded to search. Server default half-life is 14 days. See <see cref="SearchAsync"/>. May require a Scale plan or higher.</param>
    /// <param name="freshnessHalfLifeDays">Freshness decay half-life in days (&gt; 0); forwarded to search. See <see cref="SearchAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<RetrievalResult>> RetrieveAsync(
        string query,
        int k = 5,
        IReadOnlyList<string>? tags = null,
        string? entityId = null,
        string? since = null,
        string? until = null,
        int? lastNDays = null,
        float? maxDistance = null,
        IReadOnlyList<string>? anyTags = null,
        IReadOnlyList<string>? contentTypes = null,
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        double? recencyWeight = null,
        double? halfLifeDays = null,
        double? freshnessWeight = null,
        double? freshnessHalfLifeDays = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query cannot be empty", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var qs = $"q={Uri.EscapeDataString(query)}&k={k}&include_content=true";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        qs = AppendMetadataFilters(qs, anyTags, contentTypes, sources);
        qs = AppendPartition(AppendSearchFilters(qs, entityId, since, until, lastNDays, maxDistance, recencyWeight, halfLifeDays, freshnessWeight, freshnessHalfLifeDays, filter));
        var response = await RequestAsync<SearchResponse>(
            $"/search?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Deduplicate by DocId, keeping the best match (search returns
        // highest-scoring first).
        var seen = new HashSet<string>();
        var results = new List<RetrievalResult>();
        foreach (var r in response.Results)
        {
            if (!seen.Add(r.DocId)) continue;

            var content = r.Content;
            if (content == null)
            {
                var bytes = await DownloadAsync(r.DocId, cancellationToken).ConfigureAwait(false);
                content = Encoding.UTF8.GetString(bytes);
            }

            results.Add(new RetrievalResult
            {
                DocId = r.DocId,
                Score = r.Score,
                Content = content,
                Title = r.Title,
                ContentType = r.ContentType,
                Passage = r.Passage,
                EntityId = r.EntityId,
                Tags = r.Tags,
                Source = r.Source,
                Metadata = r.Metadata,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
            });
        }

        return results;
    }

    // ── Provable isolation ────────────────────────────────────────────

    /// <summary>Like <see cref="SearchAsync"/>, but also returns an isolation
    /// <see cref="SearchTrace"/> alongside the results.</summary>
    /// <remarks>
    /// Takes the same arguments as <see cref="SearchAsync"/>, and a partition handle
    /// injects the partition identically. The trace is computed from the records
    /// actually returned, so it is evidence — not intent — of which partition(s) the
    /// query touched. Under a handle the scope holds when
    /// <see cref="SearchTrace.PartitionsTouched"/> is empty or exactly the scoped
    /// partition, and <see cref="SearchTrace.CandidatesInScope"/> is the partition's
    /// own size.
    /// </remarks>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 10.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="entityId">Only match documents with this entity ID.</param>
    /// <param name="since">Only match documents created at or after this RFC 3339 timestamp (inclusive).</param>
    /// <param name="until">Only match documents created at or before this RFC 3339 timestamp (inclusive).</param>
    /// <param name="lastNDays">Only match documents created in the last N days (UTC, server clock).</param>
    /// <param name="maxDistance">Exclude results with a distance greater than this value.</param>
    /// <param name="anyTags">Only match documents carrying at least one of these tags (OR).</param>
    /// <param name="contentTypes">Only match documents whose content type is one of these (OR).</param>
    /// <param name="sources">Only match documents whose source is one of these (OR).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TracedSearch> SearchTraceAsync(
        string query,
        int k = 10,
        IReadOnlyList<string>? tags = null,
        string? entityId = null,
        string? since = null,
        string? until = null,
        int? lastNDays = null,
        float? maxDistance = null,
        IReadOnlyList<string>? anyTags = null,
        IReadOnlyList<string>? contentTypes = null,
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query cannot be empty", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var qs = $"q={Uri.EscapeDataString(query)}&k={k}&trace=true";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        qs = AppendMetadataFilters(qs, anyTags, contentTypes, sources);
        qs = AppendPartition(AppendSearchFilters(qs, entityId, since, until, lastNDays, maxDistance, filter: filter));
        var response = await RequestAsync<TracedSearchResponse>(
            $"/search?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
        StampQueryId(response.Results, response.QueryId);
        return new TracedSearch
        {
            Results = response.Results,
            Trace = response.Trace,
        };
    }

    /// <summary>Self-test that a scoped search never leaks out of this partition.
    /// Only valid on a partition handle.</summary>
    /// <remarks>
    /// Runs <see cref="SearchTraceAsync"/> under this handle's partition and checks
    /// that every returned record stayed in scope. <see cref="IsolationCheck.Ok"/> is
    /// true iff nothing leaked. Drop one assertion into your own tests to prove
    /// isolation against your data:
    /// <code>Assert.True((await client.Partition("client_42").VerifyIsolationAsync("returns policy")).Ok);</code>
    /// Only meaningful on a partition handle and for a query that returns results — a
    /// 0-result query passes vacuously.
    /// </remarks>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 10.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">This client is not a partition handle.</exception>
    public async Task<IsolationCheck> VerifyIsolationAsync(
        string query,
        int k = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_partition))
            throw new InvalidOperationException(
                "VerifyIsolation requires a partition handle — call " +
                "client.Partition(id).VerifyIsolationAsync(...)");
        var traced = await SearchTraceAsync(query, k, cancellationToken: cancellationToken).ConfigureAwait(false);
        var scoped = _partition;
        var leaked = traced.Trace.PartitionsTouched.Where(p => p != scoped).ToList();
        var ok = leaked.Count == 0 && !traced.Trace.DefaultPartitionTouched;
        return new IsolationCheck
        {
            Ok = ok,
            ScopedTo = scoped,
            PartitionsTouched = traced.Trace.PartitionsTouched,
            Results = traced.Trace.Results,
            CandidatesInScope = traced.Trace.CandidatesInScope,
            Leaked = leaked,
        };
    }

    // ── Partition lifecycle ───────────────────────────────────────────

    /// <summary>List this tenant's partitions with active document counts.</summary>
    /// <remarks>
    /// Tenant-level: this does <b>not</b> use the partition handle and returns the
    /// whole tenant's partitions (ascending by id). The result also carries advisory
    /// <see cref="PartitionList.Warnings"/> flagging likely typos or abandoned
    /// partitions; they never block anything. The default (unkeyed) partition is not listed.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PartitionList> ListPartitionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await RequestAsync<PartitionList>(
            "/partitions", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete a partition and shred <b>every</b> document in it (active and
    /// tombstoned). One-call client-offboarding / data-erasure teardown.</summary>
    /// <remarks>
    /// Tenant-level: this names the target explicitly and does <b>not</b> use the
    /// partition handle, so a destructive call can never be aimed by a forgotten or
    /// implicit scope. Idempotent: deleting an unknown or empty partition returns
    /// <c>0</c> and is never an error.
    /// </remarks>
    /// <param name="partitionId">The partition to delete. Non-empty, non-whitespace, 1–256 characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of documents deleted.</returns>
    /// <exception cref="ArgumentException">The partition ID is empty/whitespace or longer than 256 characters.</exception>
    public async Task<int> DeletePartitionAsync(
        string partitionId,
        CancellationToken cancellationToken = default)
    {
        ValidatePartitionId(partitionId);
        var response = await RequestAsync<DeletePartitionResponse>(
            $"/partitions/{Uri.EscapeDataString(partitionId)}", HttpMethod.Delete,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.DocumentsDeleted;
    }

    /// <summary>Insert a document with precomputed embeddings (BYOE — bring your own embeddings).</summary>
    /// <param name="request">The document content, passages, and/or embedding to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DocumentRecord> InsertWithEmbeddingsAsync(
        InsertWithEmbeddingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (!string.IsNullOrEmpty(_partition))
            request.Partition = _partition;
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await RequestAsync<DocumentRecord>(
            "/documents/embed", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Search by raw embedding vector (BYOE — bring your own embeddings).</summary>
    /// <param name="embedding">The query embedding vector.</param>
    /// <param name="k">Maximum number of results to return. Default: 10.</param>
    /// <param name="includeContent">Whether to include document content in results. Default: false.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="entityId">Only match documents with this entity ID.</param>
    /// <param name="since">Only match documents created at or after this RFC 3339 timestamp (inclusive).</param>
    /// <param name="until">Only match documents created at or before this RFC 3339 timestamp (inclusive).</param>
    /// <param name="lastNDays">Only match documents created in the last N days (UTC, server clock).</param>
    /// <param name="maxDistance">Exclude results with a distance greater than this value.</param>
    /// <param name="anyTags">Only match documents carrying at least one of these tags (OR).</param>
    /// <param name="contentTypes">Only match documents whose content type is one of these (OR).</param>
    /// <param name="sources">Only match documents whose source is one of these (OR).</param>
    /// <param name="recencyWeight">Blend recency into ranking, in <c>[0, 1]</c>. See <see cref="SearchAsync"/>.</param>
    /// <param name="halfLifeDays">Recency decay half-life in days (&gt; 0). See <see cref="SearchAsync"/>.</param>
    /// <param name="freshnessWeight">Blend freshness (recent updates) into ranking, in <c>[0, 1]</c>; server default half-life is 14 days. See <see cref="SearchAsync"/>. May require a Scale plan or higher.</param>
    /// <param name="freshnessHalfLifeDays">Freshness decay half-life in days (&gt; 0). See <see cref="SearchAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<SearchResult>> SearchByVectorAsync(
        float[] embedding,
        int k = 10,
        bool includeContent = false,
        IReadOnlyList<string>? tags = null,
        string? entityId = null,
        string? since = null,
        string? until = null,
        int? lastNDays = null,
        float? maxDistance = null,
        IReadOnlyList<string>? anyTags = null,
        IReadOnlyList<string>? contentTypes = null,
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        double? recencyWeight = null,
        double? halfLifeDays = null,
        double? freshnessWeight = null,
        double? freshnessHalfLifeDays = null,
        CancellationToken cancellationToken = default)
    {
        if (embedding is null || embedding.Length == 0)
            throw new ArgumentException("embedding cannot be null or empty", nameof(embedding));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var body = new VectorSearchRequest
        {
            Embedding = embedding,
            K = k,
            IncludeContent = includeContent,
            Tags = tags is { Count: > 0 } ? new List<string>(tags) : null,
            AnyTags = anyTags is { Count: > 0 } ? new List<string>(anyTags) : null,
            ContentTypes = contentTypes is { Count: > 0 } ? new List<string>(contentTypes) : null,
            Sources = sources is { Count: > 0 } ? new List<string>(sources) : null,
            EntityId = string.IsNullOrEmpty(entityId) ? null : entityId,
            Since = string.IsNullOrEmpty(since) ? null : since,
            Until = string.IsNullOrEmpty(until) ? null : until,
            LastNDays = lastNDays,
            MaxDistance = maxDistance,
            Filter = filter is { Count: > 0 }
                ? filter.ToDictionary(kv => kv.Key, kv => kv.Value)
                : null,
            RecencyWeight = recencyWeight,
            HalfLifeDays = halfLifeDays,
            FreshnessWeight = freshnessWeight,
            FreshnessHalfLifeDays = freshnessHalfLifeDays,
            Partition = string.IsNullOrEmpty(_partition) ? null : _partition,
        };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RequestAsync<SearchResponse>(
            "/search/embed", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        StampQueryId(response.Results, response.QueryId);
        return response.Results;
    }

    // ── Async Processing ──────────────────────────────────────────────

    /// <summary>Enqueue a document for asynchronous processing. Useful for large documents.</summary>
    /// <param name="entityId">Optional entity ID to associate with the document, for entity-scoped search and list filters.</param>
    /// <param name="source">Optional source label to associate with the document (e.g. its origin system), for source-scoped search and list filters.</param>
    public async Task<AsyncJobResult> EnqueueDocumentAsync(
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        string? entityId = null,
        string? source = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("filename cannot be empty", nameof(filename));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        contentType ??= GuessContentType(filename);
        var query = AppendPartition(BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking, entityId, source, metadata));
        var content = new ByteArrayContent(data);
        return await RequestAsync<AsyncJobResult>(
            $"/documents/async?{query}", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wait for a background document job to complete.</summary>
    public async Task<JobStatus> WaitForJobAsync(
        string jobId,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(jobId))
            throw new ArgumentException("jobId cannot be empty", nameof(jobId));
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        var effectivePoll = pollInterval ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + effectiveTimeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await RequestAsync<JobStatus>(
                $"/documents/jobs/{Uri.EscapeDataString(jobId)}", HttpMethod.Get,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (status.Status is "completed" or "failed")
                return status;

            await Task.Delay(effectivePoll, cancellationToken).ConfigureAwait(false);
        }

        throw new AetherApiException(
            (System.Net.HttpStatusCode)408, "Job polling timed out", "timeout");
    }

    // ── Batch ─────────────────────────────────────────────────────────

    /// <summary>Insert multiple text documents in a single batch request.</summary>
    public async Task<List<DocumentRecord>> BatchInsertAsync(
        List<BatchInsertItem> documents,
        ChunkingConfig? chunking = null,
        CancellationToken cancellationToken = default)
    {
        if (documents is null || documents.Count == 0)
            throw new ArgumentException("documents cannot be null or empty", nameof(documents));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        // When scoped, every item carries the same partition (per-item wire
        // field). Project into fresh items so the caller's input objects are
        // never mutated (a reused list stays unscoped on an unscoped client).
        var items = documents;
        if (!string.IsNullOrEmpty(_partition))
        {
            items = new List<BatchInsertItem>(documents.Count);
            foreach (var item in documents)
                items.Add(new BatchInsertItem
                {
                    Filename = item.Filename,
                    Content = item.Content,
                    Tags = item.Tags,
                    EntityId = item.EntityId,
                    Source = item.Source,
                    Metadata = item.Metadata,
                    Partition = _partition,
                });
        }
        var request = new BatchInsertRequest { Documents = items };
        if (chunking?.ChunkSize > 0)
            request.ChunkSize = chunking.ChunkSize;
        if (chunking?.Overlap > 0)
            request.Overlap = chunking.Overlap;

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RequestAsync<BatchInsertResponse>(
            "/documents/batch", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        return response.Results;
    }

    /// <summary>Run multiple search queries in a single batch request.</summary>
    public async Task<List<BatchSearchResponse>> BatchSearchAsync(
        List<BatchSearchQuery> queries,
        CancellationToken cancellationToken = default)
    {
        if (queries is null || queries.Count == 0)
            throw new ArgumentException("queries cannot be null or empty", nameof(queries));
        // When scoped, every query carries the same partition (per-query wire
        // field). Project into fresh queries so the caller's input objects are
        // never mutated (a reused list stays unscoped on an unscoped client).
        var scopedQueries = queries;
        if (!string.IsNullOrEmpty(_partition))
        {
            scopedQueries = new List<BatchSearchQuery>(queries.Count);
            foreach (var q in queries)
                scopedQueries.Add(new BatchSearchQuery
                {
                    Q = q.Q,
                    K = q.K,
                    Tags = q.Tags,
                    AnyTags = q.AnyTags,
                    ContentTypes = q.ContentTypes,
                    Sources = q.Sources,
                    Filter = q.Filter,
                    IncludeContent = q.IncludeContent,
                    EntityId = q.EntityId,
                    Since = q.Since,
                    Until = q.Until,
                    LastNDays = q.LastNDays,
                    MaxDistance = q.MaxDistance,
                    RecencyWeight = q.RecencyWeight,
                    HalfLifeDays = q.HalfLifeDays,
                    FreshnessWeight = q.FreshnessWeight,
                    FreshnessHalfLifeDays = q.FreshnessHalfLifeDays,
                    Partition = _partition,
                });
        }
        var request = new BatchSearchRequest { Queries = scopedQueries };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RequestAsync<BatchSearchResponseWrapper>(
            "/search/batch", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        // The feedback handle arrives per query; stamp it onto that query's hits.
        var results = new List<BatchSearchResponse>(response.Results.Count);
        foreach (var item in response.Results)
        {
            StampQueryId(item.Results, item.QueryId);
            results.Add(new BatchSearchResponse { Query = item.Query, Results = item.Results });
        }
        return results;
    }

    // ── Cluster ───────────────────────────────────────────────────────

    /// <summary>Get node status.</summary>
    public async Task<NodeStatus> StatusAsync(
        CancellationToken cancellationToken = default)
    {
        return await RequestAsync<NodeStatus>(
            "/status", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch the live $/GiB price for permanent archive uploads (Arweave/Irys).
    /// Mirrors the gateway's 5-minute cached upstream price. Useful for showing
    /// customers their archive cost before flipping the
    /// <c>permanentArchive</c> toggle. The server returns 404 when the gateway
    /// is configured without an upstream URL — surfaces here as
    /// <see cref="AetherApiException"/>.
    /// </summary>
    public async Task<ArchivePrice> GetArchivePriceAsync(
        CancellationToken cancellationToken = default)
    {
        return await RequestAsync<ArchivePrice>(
            "/archive/price", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Note: Cluster operations (Sync, Snapshot, Checkpoint, Recover, Validate)
    // are admin-only and not exposed in the public SDK. Use the REST API
    // directly with an admin API key for operational tasks.

    // ── IDisposable ───────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            // Only the client that built the HttpClient closes it. A partition-scoped
            // clone (or a client given an external HttpClient) shares the transport and
            // leaves it open for the owner to dispose.
            if (_ownsHttpClient)
                _http.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
