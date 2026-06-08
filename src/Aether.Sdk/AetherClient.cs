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
    private readonly string _baseUrl;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;
    private bool _disposed;

    /// <summary>SDK version, reported in the User-Agent header. Keep in sync with the csproj &lt;Version&gt;.</summary>
    private const string Version = "0.1.0";

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

    /// <summary>
    /// Joins tags into the comma-separated string the API expects on the wire.
    /// Mirrors the query-string and single-insert tag encoding; returns null when
    /// there are no tags so the field can be omitted from the JSON body.
    /// </summary>
    private static string? JoinTags(IReadOnlyList<string>? tags)
        => tags is { Count: > 0 } ? string.Join(",", tags) : null;

    private static string BuildQueryString(
        string baseQuery,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null)
    {
        var qs = baseQuery;
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        if (chunking?.ChunkSize > 0)
            qs += $"&chunk_size={chunking.ChunkSize}";
        if (chunking?.Overlap > 0)
            qs += $"&overlap={chunking.Overlap}";
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
        _baseUrl = baseUrl.TrimEnd('/');
        _maxRetries = 2;
        _retryBaseDelay = TimeSpan.FromSeconds(0.5);
        ConfigureUserAgent(_http);
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

    private async Task<T> RequestAsync<T>(
        string path,
        HttpMethod method,
        HttpContent? content = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}{path}";

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
        var url = $"{_baseUrl}{path}";

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
        var url = $"{_baseUrl}{path}";

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
    public async Task<DocumentRecord> InsertAsync(
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("filename cannot be empty", nameof(filename));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        contentType ??= GuessContentType(filename);
        var query = BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking);
        var content = new ByteArrayContent(data);
        return await RequestAsync<DocumentRecord>(
            $"/documents?{query}", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Insert raw text content.</summary>
    public async Task<DocumentRecord> InsertTextAsync(
        string text,
        string filename = "text.txt",
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("text cannot be empty", nameof(text));
        var bytes = Encoding.UTF8.GetBytes(text);
        return await InsertAsync(bytes, filename, "text/plain", tags, chunking, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Insert a document from a Stream without buffering the entire body in memory.
    /// Unlike <see cref="InsertAsync"/>, this method does not retry on transient errors
    /// because the stream may not be re-readable.</summary>
    /// <param name="stream">The document content as a readable stream.</param>
    /// <param name="filename">Filename for the document. Default: "upload.bin".</param>
    /// <param name="contentType">MIME type. Default: "application/octet-stream".</param>
    /// <param name="tags">Optional tags to associate with the document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DocumentRecord> InsertStreamAsync(
        Stream stream,
        string filename = "upload.bin",
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        contentType ??= "application/octet-stream";
        var query = BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags);
        var url = $"{_baseUrl}/documents?{query}";

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
    public async Task<DocumentRecord> UpdateAsync(
        string docId,
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
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
        var query = BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking);
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
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DocumentListResult> ListAsync(
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync<DocumentListResponse>(
            $"/documents?offset={offset}&limit={limit}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
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

    // ── Search ────────────────────────────────────────────────────────

    /// <summary>Similarity search across documents.</summary>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 10.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="maxDistance">
    /// Optional cosine-distance ceiling. Results with
    /// <c>distance &gt; maxDistance</c> are dropped server-side, after reranking.
    /// Smaller is stricter (0.0 = exact match, ~1.0 = unrelated). Pass
    /// <c>null</c> to return the top-k regardless of distance — the historical
    /// behavior.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<SearchResult>> SearchAsync(
        string query,
        int k = 10,
        IReadOnlyList<string>? tags = null,
        float? maxDistance = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query cannot be empty", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var qs = $"q={Uri.EscapeDataString(query)}&k={k}";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        if (maxDistance.HasValue)
            qs += $"&max_distance={maxDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var response = await RequestAsync<SearchResponse>(
            $"/search?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Results;
    }

    /// <summary>Search with content retrieval. Results are deduplicated by DocId (closest match wins).
    /// Falls back to <see cref="DownloadAsync"/> for results missing inline content.</summary>
    /// <param name="query">Natural language search query.</param>
    /// <param name="k">Maximum number of results to return. Default: 5.</param>
    /// <param name="tags">Optional tags to filter results.</param>
    /// <param name="maxDistance">
    /// Optional cosine-distance ceiling. See <see cref="SearchAsync"/> for
    /// semantics.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<RetrievalResult>> RetrieveAsync(
        string query,
        int k = 5,
        IReadOnlyList<string>? tags = null,
        float? maxDistance = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query))
            throw new ArgumentException("query cannot be empty", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");
        var qs = $"q={Uri.EscapeDataString(query)}&k={k}&include_content=true";
        if (tags is { Count: > 0 })
            qs += $"&tags={Uri.EscapeDataString(string.Join(",", tags))}";
        if (maxDistance.HasValue)
            qs += $"&max_distance={maxDistance.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var response = await RequestAsync<SearchResponse>(
            $"/search?{qs}", HttpMethod.Get, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Deduplicate by DocId, keeping the closest match
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
                Distance = r.Distance,
                Content = content,
                Title = r.Title,
                ContentType = r.ContentType,
                Passage = r.Passage,
            });
        }

        return results;
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
    /// <param name="maxDistance">
    /// Optional cosine-distance ceiling. See <see cref="SearchAsync"/> for
    /// semantics.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<SearchResult>> SearchByVectorAsync(
        float[] embedding,
        int k = 10,
        bool includeContent = false,
        IReadOnlyList<string>? tags = null,
        float? maxDistance = null,
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
            MaxDistance = maxDistance,
        };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RequestAsync<SearchResponse>(
            "/search/embed", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        return response.Results;
    }

    // ── Async Processing ──────────────────────────────────────────────

    /// <summary>Enqueue a document for asynchronous processing. Useful for large documents.</summary>
    public async Task<AsyncJobResult> EnqueueDocumentAsync(
        byte[] data,
        string filename,
        string? contentType = null,
        IReadOnlyList<string>? tags = null,
        ChunkingConfig? chunking = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filename))
            throw new ArgumentException("filename cannot be empty", nameof(filename));
        if (chunking?.ChunkSize < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "ChunkSize must be non-negative");
        if (chunking?.Overlap < 0)
            throw new ArgumentOutOfRangeException(nameof(chunking), "Overlap must be non-negative");
        contentType ??= GuessContentType(filename);
        var query = BuildQueryString(
            $"filename={Uri.EscapeDataString(filename)}&content_type={Uri.EscapeDataString(contentType)}",
            tags, chunking);
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
        var request = new BatchInsertRequest
        {
            // Map to the wire shape, joining each item's tags into a comma string
            // (the prod batch deserializer rejects a JSON array with 422).
            Documents = documents.ConvertAll(d => new BatchInsertWireItem
            {
                Filename = d.Filename,
                Content = d.Content,
                Tags = JoinTags(d.Tags),
            }),
        };
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
        var request = new BatchSearchRequest
        {
            // Map to the wire shape, joining each query's tags into a comma string
            // for consistency with the batch insert path and the rest of the API.
            Queries = queries.ConvertAll(q => new BatchSearchWireQuery
            {
                Q = q.Q,
                K = q.K,
                Tags = JoinTags(q.Tags),
                IncludeContent = q.IncludeContent,
                MaxDistance = q.MaxDistance,
            }),
        };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await RequestAsync<BatchSearchResponseWrapper>(
            "/search/batch", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        return response.Results;
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
            _http.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
