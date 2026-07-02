using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Mock HttpMessageHandler that returns a canned response.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        // Read the body eagerly: the client disposes the request after sending.
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return _handler(request);
    }

    public static MockHttpMessageHandler WithJson(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new MockHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        });
    }

    public static MockHttpMessageHandler WithBytes(byte[] data)
    {
        return new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data),
        });
    }
}

public class AetherClientTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    private static AetherClient CreateAuthClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "aether_testkey123");
        return new AetherClient(http, "http://localhost:9000");
    }

    // ── Auth ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendsAuthorizationHeader()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            node_id = 0,
            cluster_mode = false,
            documents = 0,
            tombstoned = 0,
            vectors = 0,
            shards = 0,
            events = 0,
            wal_size_bytes = 0,
            erasure_coding = true,
            token_balance = 0,
        });

        using var client = CreateAuthClient(handler);
        await client.StatusAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("aether_testkey123", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    // ── Error handling ────────────────────────────────────────────

    [Fact]
    public async Task ThrowsAetherApiExceptionOn401()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "Invalid API key" }, HttpStatusCode.Unauthorized);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(() => client.StatusAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("Invalid API key", ex.Body);
    }

    [Fact]
    public async Task ThrowsAetherApiExceptionOn404()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "Document not found" }, HttpStatusCode.NotFound);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(() => client.GetAsync("nonexistent"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ThrowsAetherNetworkExceptionOnConnectionFailure()
    {
        var handler = new MockHttpMessageHandler(_ =>
            throw new HttpRequestException("Connection refused"));

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<AetherNetworkException>(() => client.StatusAsync());
    }

    // Typed billing rejections driven through a real client method.
    // These exercise the full parse path (read body → deserialize {error,code} →
    // AetherApiException.FromResponse) on the mocked transport, proving the client
    // surfaces the typed subclass rather than the base AetherApiException.

    [Fact]
    public async Task ThrowsCreditExhaustedOn402WithCode()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new
            {
                error = "Prepaid credit balance exhausted; top up to continue.",
                code = "credit_exhausted",
                request_id = "req-123",
                resource = "vectors",
                balance_cents = 0,
            },
            (HttpStatusCode)402);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<CreditExhaustedException>(
            () => client.InsertTextAsync("hello"));

        Assert.Equal((HttpStatusCode)402, ex.StatusCode);
        Assert.Equal("credit_exhausted", ex.ErrorCode);
        Assert.Equal("Prepaid credit balance exhausted; top up to continue.", ex.Body);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task ThrowsTenantPausedOn403WithCode()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new
            {
                error = "Tenant has been paused by the operator",
                code = "tenant_paused",
                request_id = "req-123",
            },
            (HttpStatusCode)403);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<TenantPausedException>(
            () => client.GetAsync("abc-123"));

        Assert.Equal((HttpStatusCode)403, ex.StatusCode);
        Assert.Equal("tenant_paused", ex.ErrorCode);
        Assert.Equal("Tenant has been paused by the operator", ex.Body);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task ThrowsFreeLimitExceededOn402WithCode()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new
            {
                error = "Free vector limit exceeded (1001/1000)",
                code = "free_limit_exceeded",
                request_id = "req-123",
                limit_type = "vectors",
                plan = "free",
            },
            (HttpStatusCode)402);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<FreeLimitExceededException>(
            () => client.InsertTextAsync("hello"));

        Assert.Equal((HttpStatusCode)402, ex.StatusCode);
        Assert.Equal("free_limit_exceeded", ex.ErrorCode);
        Assert.IsNotType<CreditExhaustedException>(ex);
    }

    // ── Insert ────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_PostsBinaryWithQueryParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "blake3hash",
            chunks = 3,
            vectors = 3,
            version = 1,
        });

        using var client = CreateClient(handler);
        var data = Encoding.UTF8.GetBytes("hello world");
        var result = await client.InsertAsync(data, "test.txt", "text/plain");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("filename=test.txt", handler.LastRequest.RequestUri!.Query);
        Assert.Contains("content_type=text%2Fplain", handler.LastRequest.RequestUri.Query);
        Assert.Equal("abc-123", result.DocId);
        Assert.Equal(3, result.Chunks);
    }

    // size_bytes — the insert response carries the full document
    // record (size_bytes, title, content_type, version). Assert the insert path
    // parses those fields, not just doc_id/chunks. (Regression: earlier SDK
    // builds dropped size_bytes on the write path, always returning 0.)
    [Fact]
    public async Task Insert_ParsesSizeBytesTitleAndContentType()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "blake3hash",
            title = "Hello Doc",
            content_type = "text/plain",
            size_bytes = 11,
            chunks = 3,
            vectors = 3,
            version = 1,
        });

        using var client = CreateClient(handler);
        var data = Encoding.UTF8.GetBytes("hello world");
        var result = await client.InsertAsync(data, "test.txt", "text/plain");

        Assert.Equal(11, result.SizeBytes);
        Assert.Equal("Hello Doc", result.Title);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal(1, result.Version);
    }

    [Fact]
    public async Task InsertText_ParsesSizeBytes()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "txt-456",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 9,
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        var result = await client.InsertTextAsync("some text");

        Assert.Equal(9, result.SizeBytes);
    }

    [Fact]
    public async Task InsertStreamAsync_ParsesSizeBytes()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "stream-123",
            cid = "streamhash",
            content_type = "application/pdf",
            size_bytes = 13,
            chunks = 5,
            vectors = 5,
            version = 1,
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("streamed data"));
        var result = await client.InsertStreamAsync(stream, "upload.pdf", "application/pdf");

        Assert.Equal(13, result.SizeBytes);
        Assert.Equal("application/pdf", result.ContentType);
    }

    [Fact]
    public async Task Update_ParsesSizeBytes()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "newhash",
            content_type = "text/plain",
            size_bytes = 7,
            chunks = 4,
            vectors = 4,
            version = 2,
        });

        using var client = CreateClient(handler);
        var result = await client.UpdateAsync(
            "abc-123", Encoding.UTF8.GetBytes("updated"), "test.txt");

        Assert.Equal(7, result.SizeBytes);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task Insert_SendsEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertAsync(
            Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain", entityId: "acct/42");

        Assert.Contains("entity_id=acct%2F42", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Insert_OmitsEntityIdWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertAsync(Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain");

        Assert.DoesNotContain("entity_id", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Insert_SendsSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertAsync(
            Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain", source: "slack");

        Assert.Contains("source=slack", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Insert_OmitsSourceWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertAsync(Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain");

        Assert.DoesNotContain("source", handler.LastRequest!.RequestUri!.Query);
    }

    // The insert response echoes the document's tags and source.
    [Fact]
    public async Task Insert_ParsesTagsAndSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 1,
            tags = new[] { "a", "b" },
            source = "notion",
        });

        using var client = CreateClient(handler);
        var result = await client.InsertAsync(Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain");

        Assert.Equal(new[] { "a", "b" }, result.Tags);
        Assert.Equal("notion", result.Source);
    }

    [Fact]
    public async Task Insert_DefaultsTagsToEmptyAndSourceToNull()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        var result = await client.InsertAsync(Encoding.UTF8.GetBytes("hello"), "test.txt", "text/plain");

        Assert.Empty(result.Tags);
        Assert.Null(result.Source);
    }

    // ── InsertText ────────────────────────────────────────────────

    [Fact]
    public async Task InsertText_EncodesAsUtf8()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "txt-456",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        var result = await client.InsertTextAsync("some text");

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("filename=text.txt", handler.LastRequest!.RequestUri!.Query);
        Assert.Equal("txt-456", result.DocId);
    }

    [Fact]
    public async Task InsertText_SendsEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "txt-456",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertTextAsync("some text", entityId: "user-7");

        Assert.Contains("entity_id=user-7", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task InsertText_SendsSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "txt-456",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertTextAsync("some text", source: "email");

        Assert.Contains("source=email", handler.LastRequest!.RequestUri!.Query);
    }

    // ── InsertStream ─────────────────────────────────────────────

    [Fact]
    public async Task InsertStreamAsync_PostsWithQueryParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "stream-123",
            cid = "streamhash",
            chunks = 5,
            vectors = 5,
            version = 1,
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("streamed data"));
        var result = await client.InsertStreamAsync(stream, "upload.pdf", "application/pdf");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("filename=upload.pdf", handler.LastRequest.RequestUri!.Query);
        Assert.Contains("content_type=application%2Fpdf", handler.LastRequest.RequestUri.Query);
        Assert.Equal("stream-123", result.DocId);
        Assert.Equal(5, result.Chunks);
    }

    [Fact]
    public async Task InsertStreamAsync_UsesDefaults()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "def-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        await client.InsertStreamAsync(stream);

        Assert.Contains("filename=upload.bin", handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("content_type=application%2Foctet-stream", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task InsertStreamAsync_SendsEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "stream-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        await client.InsertStreamAsync(stream, "test.txt", "text/plain", entityId: "acct/42");

        Assert.Contains("entity_id=acct%2F42", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task InsertStreamAsync_SendsSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "stream-123",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        await client.InsertStreamAsync(stream, "test.txt", "text/plain", source: "upload");

        Assert.Contains("source=upload", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task InsertStreamAsync_DoesNotRetry()
    {
        var callCount = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { error = "Service Unavailable" }),
                    Encoding.UTF8, "application/json"),
            };
        });

        using var client = CreateClient(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        await Assert.ThrowsAsync<AetherApiException>(
            () => client.InsertStreamAsync(stream, "test.txt", "text/plain"));

        Assert.Equal(1, callCount);
    }

    // ── Update ────────────────────────────────────────────────────

    [Fact]
    public async Task Update_PutsToDocumentId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "newhash",
            chunks = 4,
            vectors = 4,
            version = 2,
        });

        using var client = CreateClient(handler);
        var result = await client.UpdateAsync(
            "abc-123", Encoding.UTF8.GetBytes("updated"), "test.txt");

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Contains("/v1/documents/abc-123", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(2, result.Version);
    }

    [Fact]
    public async Task Update_SendsEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "newhash",
            chunks = 4,
            vectors = 4,
            version = 2,
        });

        using var client = CreateClient(handler);
        await client.UpdateAsync(
            "abc-123", Encoding.UTF8.GetBytes("updated"), "test.txt", entityId: "acct/42");

        Assert.Contains("entity_id=acct%2F42", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Update_SendsSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "newhash",
            chunks = 4,
            vectors = 4,
            version = 2,
        });

        using var client = CreateClient(handler);
        await client.UpdateAsync(
            "abc-123", Encoding.UTF8.GetBytes("updated"), "test.txt", source: "slack");

        Assert.Contains("source=slack", handler.LastRequest!.RequestUri!.Query);
    }

    // ── Get ───────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsDocumentRecord()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            title = "Test Doc",
            content_type = "text/plain",
            size_bytes = 1024,
            chunks = 3,
            vectors = 3,
            version = 1,
            created_at = "2024-01-01T00:00:00Z",
        });

        using var client = CreateClient(handler);
        var doc = await client.GetAsync("abc-123");

        Assert.Contains("/v1/documents/abc-123", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Test Doc", doc.Title);
        Assert.Equal(1024, doc.SizeBytes);
    }

    [Fact]
    public async Task Get_DeserializesEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 1024,
            chunks = 3,
            vectors = 3,
            version = 1,
            entity_id = "acct/42",
            created_at = "2026-06-01T00:00:00Z",
        });

        using var client = CreateClient(handler);
        var doc = await client.GetAsync("abc-123");

        Assert.Equal("acct/42", doc.EntityId);
    }

    [Fact]
    public async Task Get_DeserializesTagsAndSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 1024,
            chunks = 3,
            vectors = 3,
            version = 1,
            tags = new[] { "x", "y" },
            source = "notion",
            created_at = "2026-06-01T00:00:00Z",
        });

        using var client = CreateClient(handler);
        var doc = await client.GetAsync("abc-123");

        Assert.Equal(new[] { "x", "y" }, doc.Tags);
        Assert.Equal("notion", doc.Source);
    }

    // ── Download ──────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReturnsBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var handler = MockHttpMessageHandler.WithBytes(bytes);

        using var client = CreateClient(handler);
        var result = await client.DownloadAsync("abc-123");

        Assert.Equal(bytes, result);
    }

    // ── List ──────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsDocumentArray()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = new[]
            {
                new { doc_id = "a", cid = "", content_type = "text/plain", size_bytes = 100, version = 1 },
                new { doc_id = "b", cid = "", content_type = "application/pdf", size_bytes = 200, version = 2 },
            },
            count = 2,
        });

        using var client = CreateClient(handler);
        var docs = await client.ListAsync();

        Assert.Equal(2, docs.Documents.Count);
        Assert.Equal("a", docs.Documents[0].DocId);
        Assert.Equal("b", docs.Documents[1].DocId);
    }

    [Fact]
    public async Task List_PassesFilters()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = Array.Empty<object>(),
            count = 0,
        });

        using var client = CreateClient(handler);
        await client.ListAsync(
            entityId: "acct/42",
            since: "2026-06-01T00:00:00Z",
            until: "2026-06-10T23:59:59Z",
            lastNDays: 7);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("entity_id=acct%2F42", query);
        Assert.Contains("since=2026-06-01T00%3A00%3A00Z", query);
        Assert.Contains("until=2026-06-10T23%3A59%3A59Z", query);
        Assert.Contains("last_n_days=7", query);
    }

    [Fact]
    public async Task List_OmitsFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = Array.Empty<object>(),
            count = 0,
        });

        using var client = CreateClient(handler);
        await client.ListAsync();

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("offset=0", query);
        Assert.Contains("limit=50", query);
        Assert.DoesNotContain("entity_id", query);
        Assert.DoesNotContain("since", query);
        Assert.DoesNotContain("until", query);
        Assert.DoesNotContain("last_n_days", query);
    }

    [Fact]
    public async Task List_PassesMetadataFilters()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = Array.Empty<object>(),
            count = 0,
        });

        using var client = CreateClient(handler);
        await client.ListAsync(
            tags: new[] { "must", "have" },
            anyTags: new[] { "a", "b" },
            contentTypes: new[] { "text/plain" },
            sources: new[] { "slack", "notion" });

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("tags=must%2Chave", query);
        Assert.Contains("any_tags=a%2Cb", query);
        Assert.Contains("content_type=text%2Fplain", query);
        Assert.Contains("source=slack%2Cnotion", query);
    }

    [Fact]
    public async Task List_OmitsMetadataFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = Array.Empty<object>(),
            count = 0,
        });

        using var client = CreateClient(handler);
        await client.ListAsync();

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("tags=", query);
        Assert.DoesNotContain("any_tags", query);
        Assert.DoesNotContain("content_type", query);
        Assert.DoesNotContain("source", query);
    }

    [Fact]
    public async Task List_ParsesTagsAndSourceOnDocuments()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = new[]
            {
                new
                {
                    doc_id = "a",
                    cid = "",
                    content_type = "text/plain",
                    size_bytes = 100,
                    version = 1,
                    tags = new[] { "t1" },
                    source = "slack",
                },
            },
            count = 1,
        });

        using var client = CreateClient(handler);
        var docs = await client.ListAsync();

        Assert.Single(docs.Documents);
        Assert.Equal(new[] { "t1" }, docs.Documents[0].Tags);
        Assert.Equal("slack", docs.Documents[0].Source);
    }

    // ── Delete ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SendsDeleteRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", doc_id = "abc-123" });

        using var client = CreateClient(handler);
        await client.DeleteAsync("abc-123");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("/v1/documents/abc-123", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("hard", handler.LastRequest.RequestUri.Query); // soft by default
    }

    // HardDeleteAsync issues DELETE with ?hard=true (irreversible purge).
    [Fact]
    public async Task HardDelete_SendsHardFlag()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "hard_deleted", doc_id = "abc-123" });

        using var client = CreateClient(handler);
        await client.HardDeleteAsync("abc-123");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("/v1/documents/abc-123", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("hard=true", handler.LastRequest.RequestUri.Query);
    }

    // ── Restore ───────────────────────────────────────────────────

    [Fact]
    public async Task Restore_PostsToRestore()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "restored", doc_id = "abc-123" });

        using var client = CreateClient(handler);
        await client.RestoreAsync("abc-123");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("/v1/documents/abc-123/restore", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    // ── Backfill entity ───────────────────────────────────────────

    [Fact]
    public async Task BackfillEntityFromTags_PostsTagPrefixAndOverwrite()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            scanned = 0,
            updated = 0,
            skipped_existing = 0,
            skipped_no_match = 0,
            skipped_ambiguous = 0,
            skipped_invalid = 0,
        });

        using var client = CreateClient(handler);
        await client.BackfillEntityFromTagsAsync("patient:");

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/documents/backfill-entity", handler.LastRequest.RequestUri!.AbsolutePath);

        var body = handler.LastRequestBody!;
        Assert.Contains("\"tag_prefix\":\"patient:\"", body);
        Assert.Contains("\"overwrite\":false", body);
    }

    [Fact]
    public async Task BackfillEntityFromTags_ForwardsOverwriteTrue()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            scanned = 0,
            updated = 0,
            skipped_existing = 0,
            skipped_no_match = 0,
            skipped_ambiguous = 0,
            skipped_invalid = 0,
        });

        using var client = CreateClient(handler);
        await client.BackfillEntityFromTagsAsync("patient:", overwrite: true);

        Assert.Contains("\"overwrite\":true", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BackfillEntityFromTags_DeserializesReport()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            scanned = 100,
            updated = 60,
            skipped_existing = 20,
            skipped_no_match = 12,
            skipped_ambiguous = 5,
            skipped_invalid = 3,
        });

        using var client = CreateClient(handler);
        var report = await client.BackfillEntityFromTagsAsync("patient:");

        Assert.Equal(100, report.Scanned);
        Assert.Equal(60, report.Updated);
        Assert.Equal(20, report.SkippedExisting);
        Assert.Equal(12, report.SkippedNoMatch);
        Assert.Equal(5, report.SkippedAmbiguous);
        Assert.Equal(3, report.SkippedInvalid);
    }

    [Fact]
    public async Task BackfillEntityFromTags_ThrowsOnEmptyPrefix()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.BackfillEntityFromTagsAsync(""));
    }

    // ── Search ────────────────────────────────────────────────────

    [Fact]
    public async Task Search_PassesQueryAndK()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "machine learning",
            results = new[]
            {
                new { doc_id = "abc", score = 85, title = "ML Intro", content_type = "text/plain" },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("machine learning", 5);

        Assert.Contains("q=machine%20learning", handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("k=5", handler.LastRequest.RequestUri.Query);
        Assert.Single(results);
        Assert.Equal(85, results[0].Score);
    }

    [Fact]
    public async Task Search_DefaultsKTo10()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        Assert.Contains("k=10", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Search_PassesEntityAndTimeFilters()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync(
            "test",
            entityId: "acct/42",
            since: "2026-06-01T00:00:00Z",
            until: "2026-06-10T23:59:59Z",
            lastNDays: 7,
            maxDistance: 0.5f);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("entity_id=acct%2F42", query);
        Assert.Contains("since=2026-06-01T00%3A00%3A00Z", query);
        Assert.Contains("until=2026-06-10T23%3A59%3A59Z", query);
        Assert.Contains("last_n_days=7", query);
        Assert.Contains("max_distance=0.5", query);
    }

    [Fact]
    public async Task Search_OmitsFilterParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("entity_id", query);
        Assert.DoesNotContain("since", query);
        Assert.DoesNotContain("until", query);
        Assert.DoesNotContain("last_n_days", query);
        Assert.DoesNotContain("max_distance", query);
    }

    [Fact]
    public async Task Search_PassesMetadataFilters()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync(
            "test",
            anyTags: new[] { "a", "b" },
            contentTypes: new[] { "text/plain", "application/pdf" },
            sources: new[] { "slack", "notion" });

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("any_tags=a%2Cb", query);
        Assert.Contains("content_type=text%2Fplain%2Capplication%2Fpdf", query);
        Assert.Contains("source=slack%2Cnotion", query);
    }

    [Fact]
    public async Task Search_OmitsMetadataFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("any_tags", query);
        Assert.DoesNotContain("content_type", query);
        Assert.DoesNotContain("source", query);
    }

    // The search hit echoes tags, source, and created_at.
    [Fact]
    public async Task Search_ParsesTagsSourceAndCreatedAt()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "test",
            results = new[]
            {
                new
                {
                    doc_id = "abc",
                    score = 90,
                    content_type = "text/plain",
                    tags = new[] { "a", "b" },
                    source = "slack",
                    created_at = "2026-06-01T00:00:00Z",
                },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("test");

        Assert.Single(results);
        Assert.Equal(new[] { "a", "b" }, results[0].Tags);
        Assert.Equal("slack", results[0].Source);
        Assert.Equal("2026-06-01T00:00:00Z", results[0].CreatedAt);
    }

    [Fact]
    public async Task Search_DefaultsTagsToEmptyAndSourceCreatedAtToNull()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "test",
            results = new[]
            {
                new { doc_id = "abc", score = 90, content_type = "text/plain" },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("test");

        Assert.Single(results);
        Assert.Empty(results[0].Tags);
        Assert.Null(results[0].Source);
        Assert.Null(results[0].CreatedAt);
    }

    // ── Score + updated_at parsing ─────────────────────────────────

    // The engine serves a calibrated integer `score` (0–100, higher = better)
    // per hit, plus created_at/updated_at without a second round-trip.
    [Fact]
    public async Task Search_ReadsScoreAndTimestamps()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "test",
            results = new[]
            {
                new
                {
                    doc_id = "abc",
                    score = 90,
                    content_type = "text/plain",
                    created_at = "2026-06-01T00:00:00Z",
                    updated_at = "2026-06-15T12:30:00Z",
                },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("test");

        Assert.Single(results);
        Assert.Equal(90, results[0].Score);
        Assert.Equal("2026-06-01T00:00:00Z", results[0].CreatedAt);
        Assert.Equal("2026-06-15T12:30:00Z", results[0].UpdatedAt);
    }

    // The score spans the full 0–100 range; both extremes parse as-is.
    [Fact]
    public async Task Search_ParsesFullAndZeroScores()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "test",
            results = new[]
            {
                new { doc_id = "best", score = 100, content_type = "text/plain" },
                new { doc_id = "worst", score = 0, content_type = "text/plain" },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("test");

        Assert.Equal(100, results[0].Score);
        Assert.Equal(0, results[1].Score);
    }

    // The hit echoes entity_id when the document was written under an entity.
    [Fact]
    public async Task Search_ParsesEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "test",
            results = new[]
            {
                new { doc_id = "abc", score = 90, content_type = "text/plain", entity_id = "acct/42" },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("test");

        Assert.Single(results);
        Assert.Equal("acct/42", results[0].EntityId);
    }

    // The recency knobs are forwarded on the wire as recency_weight / half_life_days
    // (snake_case), formatted with the invariant culture like max_distance.
    [Fact]
    public async Task Search_ForwardsRecencyParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test", recencyWeight: 0.25, halfLifeDays: 14.5);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("recency_weight=0.25", query);
        Assert.Contains("half_life_days=14.5", query);
    }

    [Fact]
    public async Task Search_OmitsRecencyParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("recency_weight", query);
        Assert.DoesNotContain("half_life_days", query);
    }

    [Fact]
    public async Task Retrieve_ForwardsRecencyParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.RetrieveAsync("test", recencyWeight: 0.5, halfLifeDays: 30.0);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("recency_weight=0.5", query);
        Assert.Contains("half_life_days=30", query);
    }

    // The RAG retrieval result carries the score and updated_at.
    [Fact]
    public async Task Retrieve_CarriesScoreAndPropagatesUpdatedAt()
    {
        var searchJson = JsonSerializer.Serialize(new
        {
            query = "test",
            results = new[]
            {
                new
                {
                    doc_id = "abc",
                    score = 80,
                    content_type = "text/plain",
                    created_at = "2026-06-01T00:00:00Z",
                    updated_at = "2026-06-20T09:00:00Z",
                },
            },
        });
        var handler = new MockHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/download")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("full text")),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchJson, Encoding.UTF8, "application/json"),
                });

        using var client = CreateClient(handler);
        var results = await client.RetrieveAsync("test");

        Assert.Single(results);
        Assert.Equal(80, results[0].Score);
        Assert.Equal("full text", results[0].Content);
        Assert.Equal("2026-06-01T00:00:00Z", results[0].CreatedAt);
        Assert.Equal("2026-06-20T09:00:00Z", results[0].UpdatedAt);
    }

    [Fact]
    public async Task SearchByVector_ForwardsRecencyParamsInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f }, recencyWeight: 0.3, halfLifeDays: 7.0);

        var body = handler.LastRequestBody!;
        Assert.Contains("\"recency_weight\":0.3", body);
        Assert.Contains("\"half_life_days\":7", body);
    }

    [Fact]
    public async Task SearchByVector_OmitsRecencyParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("recency_weight", body);
        Assert.DoesNotContain("half_life_days", body);
    }

    [Fact]
    public async Task BatchSearchAsync_ForwardsRecencyParamsInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5, RecencyWeight = 0.4, HalfLifeDays = 10.0 },
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"recency_weight\":0.4", body);
        Assert.Contains("\"half_life_days\":10", body);
    }

    [Fact]
    public async Task BatchSearchAsync_OmitsRecencyParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("recency_weight", body);
        Assert.DoesNotContain("half_life_days", body);
    }

    // The freshness knobs are forwarded on the wire as freshness_weight /
    // freshness_half_life_days (snake_case), the same plumbing as recency.
    [Fact]
    public async Task Search_ForwardsFreshnessParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test", freshnessWeight: 0.25, freshnessHalfLifeDays: 7.5);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("freshness_weight=0.25", query);
        Assert.Contains("freshness_half_life_days=7.5", query);
    }

    [Fact]
    public async Task Search_OmitsFreshnessParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("freshness_weight", query);
        Assert.DoesNotContain("freshness_half_life_days", query);
    }

    [Fact]
    public async Task Retrieve_ForwardsFreshnessParams()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.RetrieveAsync("test", freshnessWeight: 0.5, freshnessHalfLifeDays: 14.0);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("freshness_weight=0.5", query);
        Assert.Contains("freshness_half_life_days=14", query);
    }

    [Fact]
    public async Task SearchByVector_ForwardsFreshnessParamsInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f }, freshnessWeight: 0.3, freshnessHalfLifeDays: 3.0);

        var body = handler.LastRequestBody!;
        Assert.Contains("\"freshness_weight\":0.3", body);
        Assert.Contains("\"freshness_half_life_days\":3", body);
    }

    [Fact]
    public async Task SearchByVector_OmitsFreshnessParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("freshness_weight", body);
        Assert.DoesNotContain("freshness_half_life_days", body);
    }

    [Fact]
    public async Task BatchSearchAsync_ForwardsFreshnessParamsInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5, FreshnessWeight = 0.4, FreshnessHalfLifeDays = 9.0 },
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"freshness_weight\":0.4", body);
        Assert.Contains("\"freshness_half_life_days\":9", body);
    }

    [Fact]
    public async Task BatchSearchAsync_OmitsFreshnessParamsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("freshness_weight", body);
        Assert.DoesNotContain("freshness_half_life_days", body);
    }

    // ── Retrieve ──────────────────────────────────────────────────

    [Fact]
    public async Task Retrieve_ForwardsFiltersToSearch()
    {
        var searchJson = JsonSerializer.Serialize(new
        {
            query = "test",
            results = new[]
            {
                new { doc_id = "abc", score = 90, content_type = "text/plain" },
            },
        });
        string? searchQuery = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/download"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("full text")),
                };
            searchQuery = req.RequestUri.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson, Encoding.UTF8, "application/json"),
            };
        });

        using var client = CreateClient(handler);
        var results = await client.RetrieveAsync(
            "test",
            entityId: "acct/42",
            since: "2026-06-01T00:00:00Z",
            until: "2026-06-10T23:59:59Z",
            lastNDays: 7,
            maxDistance: 0.5f);

        Assert.NotNull(searchQuery);
        Assert.Contains("entity_id=acct%2F42", searchQuery);
        Assert.Contains("since=2026-06-01T00%3A00%3A00Z", searchQuery);
        Assert.Contains("until=2026-06-10T23%3A59%3A59Z", searchQuery);
        Assert.Contains("last_n_days=7", searchQuery);
        Assert.Contains("max_distance=0.5", searchQuery);
        Assert.Single(results);
        Assert.Equal("full text", results[0].Content);
    }

    [Fact]
    public async Task Retrieve_PassesMetadataFilters()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.RetrieveAsync(
            "test",
            anyTags: new[] { "a" },
            contentTypes: new[] { "text/plain" },
            sources: new[] { "slack" });

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("any_tags=a", query);
        Assert.Contains("content_type=text%2Fplain", query);
        Assert.Contains("source=slack", query);
    }

    [Fact]
    public async Task Retrieve_PropagatesTagsSourceAndCreatedAt()
    {
        var searchJson = JsonSerializer.Serialize(new
        {
            query = "test",
            results = new[]
            {
                new
                {
                    doc_id = "abc",
                    score = 90,
                    content_type = "text/plain",
                    tags = new[] { "a", "b" },
                    source = "notion",
                    created_at = "2026-06-01T00:00:00Z",
                },
            },
        });
        var handler = new MockHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/download")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("full text")),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchJson, Encoding.UTF8, "application/json"),
                });

        using var client = CreateClient(handler);
        var results = await client.RetrieveAsync("test");

        Assert.Single(results);
        Assert.Equal(new[] { "a", "b" }, results[0].Tags);
        Assert.Equal("notion", results[0].Source);
        Assert.Equal("2026-06-01T00:00:00Z", results[0].CreatedAt);
    }

    // ── BYOE ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchByVector_SendsFiltersInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(
            new[] { 0.1f, 0.2f },
            k: 5,
            entityId: "acct/42",
            since: "2026-06-01T00:00:00Z",
            until: "2026-06-10T23:59:59Z",
            lastNDays: 7,
            maxDistance: 0.5f);

        var body = handler.LastRequestBody!;
        Assert.Contains("\"entity_id\":\"acct/42\"", body);
        Assert.Contains("\"since\":\"2026-06-01T00:00:00Z\"", body);
        Assert.Contains("\"until\":\"2026-06-10T23:59:59Z\"", body);
        Assert.Contains("\"last_n_days\":7", body);
        Assert.Contains("\"max_distance\":0.5", body);
    }

    [Fact]
    public async Task SearchByVector_OmitsFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("entity_id", body);
        Assert.DoesNotContain("since", body);
        Assert.DoesNotContain("until", body);
        Assert.DoesNotContain("last_n_days", body);
        Assert.DoesNotContain("max_distance", body);
    }

    // On /search/embed the OR-list filters are sent as JSON arrays.
    [Fact]
    public async Task SearchByVector_SendsMetadataFiltersAsArraysInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(
            new[] { 0.1f, 0.2f },
            anyTags: new[] { "a", "b" },
            contentTypes: new[] { "text/plain" },
            sources: new[] { "slack" });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"any_tags\":[\"a\",\"b\"]", body);
        Assert.Contains("\"content_type\":[\"text/plain\"]", body);
        Assert.Contains("\"source\":[\"slack\"]", body);
    }

    [Fact]
    public async Task SearchByVector_OmitsMetadataFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchByVectorAsync(new[] { 0.1f, 0.2f });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("any_tags", body);
        Assert.DoesNotContain("content_type", body);
        Assert.DoesNotContain("source", body);
    }

    [Fact]
    public async Task InsertWithEmbeddings_SendsSourceInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "emb-1",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertWithEmbeddingsAsync(new InsertWithEmbeddingsRequest
        {
            Content = "hello",
            Embedding = new[] { 0.1f, 0.2f },
            Source = "notion",
        });

        Assert.Contains("\"source\":\"notion\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task InsertWithEmbeddings_OmitsSourceWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "emb-1",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertWithEmbeddingsAsync(new InsertWithEmbeddingsRequest
        {
            Content = "hello",
            Embedding = new[] { 0.1f, 0.2f },
        });

        Assert.DoesNotContain("source", handler.LastRequestBody!);
    }

    [Fact]
    public async Task InsertWithEmbeddings_SendsEntityIdInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "emb-1",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertWithEmbeddingsAsync(new InsertWithEmbeddingsRequest
        {
            Content = "hello",
            Embedding = new[] { 0.1f, 0.2f },
            EntityId = "acct/42",
        });

        Assert.Contains("\"entity_id\":\"acct/42\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task InsertWithEmbeddings_OmitsEntityIdWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "emb-1",
            cid = "hash",
            chunks = 1,
            vectors = 1,
            version = 1,
        });

        using var client = CreateClient(handler);
        await client.InsertWithEmbeddingsAsync(new InsertWithEmbeddingsRequest
        {
            Content = "hello",
            Embedding = new[] { 0.1f, 0.2f },
        });

        Assert.DoesNotContain("entity_id", handler.LastRequestBody!);
    }

    // ── Status ────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ReturnsNodeStatus()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            node_id = 0,
            documents = 42,
            vectors = 100,
            version = "0.1.0",
        });

        using var client = CreateClient(handler);
        var s = await client.StatusAsync();

        Assert.Equal(42, s.Documents);
        Assert.Equal(100, s.Vectors);
    }

    // ── Base URL handling ─────────────────────────────────────────

    [Fact]
    public async Task StripsTrailingSlashesFromBaseUrl()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            node_id = 0,
            cluster_mode = false,
            documents = 0,
            tombstoned = 0,
            vectors = 0,
            shards = 0,
            events = 0,
            wal_size_bytes = 0,
            erasure_coding = true,
            token_balance = 0,
        });

        using var client = new AetherClient(new HttpClient(handler), "http://localhost:9000///");
        await client.StatusAsync();

        Assert.Equal("http://localhost:9000/status", handler.LastRequest!.RequestUri!.ToString());
    }

    // ── Retry logic ──────────────────────────────────────────────────

    [Fact]
    public async Task RetryOnTransientError()
    {
        int attempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"unavailable\"}", Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { node_id = 1, documents = 0, vectors = 0 }),
                    Encoding.UTF8, "application/json"),
            };
        });

        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");
        var status = await client.StatusAsync();
        Assert.Equal(1, status.NodeId);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NoRetryOn404()
    {
        int attempts = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
            };
        });

        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");
        await Assert.ThrowsAsync<AetherApiException>(() => client.GetAsync("missing"));
        Assert.Equal(1, attempts);
    }

    // ── Batch operations ─────────────────────────────────────────────

    [Fact]
    public async Task BatchInsertAsync_SendsCorrectRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        var client = CreateClient(handler);

        var docs = await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
        });

        Assert.Single(docs);
        Assert.Equal("b1", docs[0].DocId);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("/v1/documents/batch", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task BatchInsertAsync_SendsEntityIdInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello", EntityId = "acct/42" },
        });

        Assert.Contains("\"entity_id\":\"acct/42\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BatchInsertAsync_OmitsEntityIdWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
        });

        Assert.DoesNotContain("entity_id", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BatchInsertAsync_SendsSourceInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello", Source = "slack" },
        });

        Assert.Contains("\"source\":\"slack\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BatchInsertAsync_OmitsSourceWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
        });

        Assert.DoesNotContain("source", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BatchSearchAsync_SendsCorrectRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = new[] { new { doc_id = "a", score = 90, content_type = "text/plain" } } } },
        });
        var client = CreateClient(handler);

        var results = await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        Assert.Single(results);
        Assert.Equal("test", results[0].Query);
    }

    [Fact]
    public async Task BatchSearchAsync_SendsFiltersInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new()
            {
                Q = "test",
                K = 5,
                EntityId = "acct/42",
                Since = "2026-06-01T00:00:00Z",
                Until = "2026-06-10T23:59:59Z",
                LastNDays = 7,
                MaxDistance = 0.5f,
            },
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"entity_id\":\"acct/42\"", body);
        Assert.Contains("\"since\":\"2026-06-01T00:00:00Z\"", body);
        Assert.Contains("\"until\":\"2026-06-10T23:59:59Z\"", body);
        Assert.Contains("\"last_n_days\":7", body);
        Assert.Contains("\"max_distance\":0.5", body);
    }

    [Fact]
    public async Task BatchSearchAsync_OmitsFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("entity_id", body);
        Assert.DoesNotContain("since", body);
        Assert.DoesNotContain("until", body);
        Assert.DoesNotContain("last_n_days", body);
        Assert.DoesNotContain("max_distance", body);
    }

    // On /search/batch the per-query metadata filters are sent as comma-joined
    // strings, the same CSV convention as the GET /search query params (the
    // engine deserializes each batch filter, tags included, as a single string).
    [Fact]
    public async Task BatchSearchAsync_SendsMetadataFiltersAsCsvInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new()
            {
                Q = "test",
                K = 5,
                Tags = new List<string> { "must", "have" },
                AnyTags = new List<string> { "a", "b" },
                ContentTypes = new List<string> { "text/plain", "application/pdf" },
                Sources = new List<string> { "slack", "notion" },
            },
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"tags\":\"must,have\"", body);
        Assert.Contains("\"any_tags\":\"a,b\"", body);
        Assert.Contains("\"content_type\":\"text/plain,application/pdf\"", body);
        Assert.Contains("\"source\":\"slack,notion\"", body);
        // Must be a CSV string, never a JSON array — the engine takes Option<String>.
        Assert.DoesNotContain("\"tags\":[", body);
    }

    [Fact]
    public async Task BatchSearchAsync_OmitsTagsWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        Assert.DoesNotContain("\"tags\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task BatchSearchAsync_OmitsMetadataFiltersWhenUnset()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        var body = handler.LastRequestBody!;
        Assert.DoesNotContain("any_tags", body);
        Assert.DoesNotContain("content_type", body);
        Assert.DoesNotContain("source", body);
    }

    // ── Async job operations ─────────────────────────────────────────

    [Fact]
    public async Task EnqueueDocumentAsync_SendsCorrectRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new { job_id = "j1", status = "pending", poll_url = "/v1/documents/jobs/j1" });
        var client = CreateClient(handler);

        var result = await client.EnqueueDocumentAsync(new byte[] { 1, 2, 3 }, "test.bin");

        Assert.Equal("j1", result.JobId);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/v1/documents/async", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task EnqueueDocumentAsync_SendsEntityId()
    {
        var handler = MockHttpMessageHandler.WithJson(new { job_id = "j1", status = "pending", poll_url = "/v1/documents/jobs/j1" });
        var client = CreateClient(handler);

        await client.EnqueueDocumentAsync(new byte[] { 1, 2, 3 }, "test.bin", entityId: "acct/42");

        Assert.Contains("entity_id=acct%2F42", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task EnqueueDocumentAsync_SendsSource()
    {
        var handler = MockHttpMessageHandler.WithJson(new { job_id = "j1", status = "pending", poll_url = "/v1/documents/jobs/j1" });
        var client = CreateClient(handler);

        await client.EnqueueDocumentAsync(new byte[] { 1, 2, 3 }, "test.bin", source: "slack");

        Assert.Contains("source=slack", handler.LastRequest!.RequestUri!.Query);
    }

    // ── Input validation ─────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ThrowsOnEmptyDocId()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync(""));
    }

    [Fact]
    public async Task SearchAsync_ThrowsOnInvalidK()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SearchAsync("test", k: 0));
    }

    [Fact]
    public async Task SearchAsync_ThrowsOnEmptyQuery()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(""));
    }

    [Fact]
    public async Task BatchInsertAsync_ThrowsOnEmptyDocuments()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.BatchInsertAsync(new List<BatchInsertItem>()));
    }

    [Fact]
    public async Task BatchSearchAsync_ThrowsOnEmptyQueries()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.BatchSearchAsync(new List<BatchSearchQuery>()));
    }

    // ── Partition scoping ────────────────────────────────────

    private static MockHttpMessageHandler InsertOkHandler() =>
        MockHttpMessageHandler.WithJson(new
        {
            doc_id = "p-1",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 1,
        });

    [Fact]
    public void Partition_ReturnsDistinctScopedClient_OriginalStaysUnscoped()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        var scoped = client.Partition("tenant-x");

        Assert.NotSame(client, scoped);
        Assert.IsType<AetherClient>(scoped);
    }

    [Fact]
    public async Task Partition_OriginalClientSendsNoPartition()
    {
        // The unscoped parent must behave byte-identically to today: no partition param.
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);
        _ = client.Partition("tenant-x"); // deriving a handle must not mutate the parent

        await client.InsertTextAsync("hello");

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_Search_SendsPartitionQueryParam()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").SearchAsync("test");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_InsertText_SendsPartitionQueryParam()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").InsertTextAsync("hello");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_Insert_SendsPartitionQueryParam()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").InsertAsync(
            Encoding.UTF8.GetBytes("hello"), "a.txt", "text/plain");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_InsertStream_SendsPartitionQueryParam()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data"));
        await client.Partition("tenant-x").InsertStreamAsync(stream, "a.bin");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_Update_SendsPartitionQueryParam()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").UpdateAsync(
            "abc-123", Encoding.UTF8.GetBytes("updated"), "a.txt");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_Enqueue_SendsPartitionQueryParam()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { job_id = "j1", status = "pending", poll_url = "/v1/documents/jobs/j1" });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").EnqueueDocumentAsync(new byte[] { 1, 2, 3 }, "a.bin");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_List_SendsPartitionQueryParam()
    {
        var handler = MockHttpMessageHandler.WithJson(new { documents = Array.Empty<object>(), count = 0 });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").ListAsync();

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_Retrieve_SendsPartitionQueryParam()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").RetrieveAsync("test");

        Assert.Contains("partition=tenant-x", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_UrlEncodesValue()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.Partition("acme/eu").SearchAsync("test");

        // Same encoding as entity_id ('/' → %2F).
        Assert.Contains("partition=acme%2Feu", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_SearchByVector_SendsPartitionInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").SearchByVectorAsync(new[] { 0.1f, 0.2f });

        Assert.Contains("\"partition\":\"tenant-x\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Partition_InsertWithEmbeddings_SendsPartitionInBody()
    {
        var handler = InsertOkHandler();
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").InsertWithEmbeddingsAsync(new InsertWithEmbeddingsRequest
        {
            Content = "hello",
            Embedding = new[] { 0.1f, 0.2f },
        });

        Assert.Contains("\"partition\":\"tenant-x\"", handler.LastRequestBody!);
    }

    [Fact]
    public async Task Partition_BatchInsert_SendsPartitionOnEveryItem()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
            new() { Filename = "b.txt", Content = "world" },
        });

        // Same partition appears once per item.
        var body = handler.LastRequestBody!;
        var occurrences = body.Split(new[] { "\"partition\":\"tenant-x\"" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public async Task Partition_BatchSearch_SendsPartitionOnEveryQuery()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = Array.Empty<object>() } },
        });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "a", K = 5 },
            new() { Q = "b", K = 5 },
        });

        var body = handler.LastRequestBody!;
        var occurrences = body.Split(new[] { "\"partition\":\"tenant-x\"" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public async Task Partition_Batch_DoesNotMutateCallerItems()
    {
        // A scoped batch call must not write partition onto the caller's input
        // objects — projecting into fresh wire items keeps a reused list usable
        // on an unscoped client afterward (parity with Python/TS/Go).
        var insertHandler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1, content_type = "text/plain", size_bytes = 5 } },
        });
        using var client = CreateClient(insertHandler);

        var items = new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
            new() { Filename = "b.txt", Content = "world" },
        };
        await client.Partition("tenant-x").BatchInsertAsync(items);
        Assert.All(items, i => Assert.Null(i.Partition));

        var queries = new List<BatchSearchQuery> { new() { Q = "a", K = 5 } };
        var searchHandler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "a", results = Array.Empty<object>() } },
        });
        using var searchClient = CreateClient(searchHandler);
        await searchClient.Partition("tenant-x").BatchSearchAsync(queries);
        Assert.All(queries, q => Assert.Null(q.Partition));
    }

    [Fact]
    public async Task Partition_DocIdMethods_SendNoPartition_Get()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 1,
            chunks = 1,
            vectors = 1,
            version = 1,
        });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").GetAsync("abc-123");

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Partition_DocIdMethods_SendNoPartition_Delete()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", doc_id = "abc-123" });
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").DeleteAsync("abc-123");

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public void Partition_RejectsEmpty_NoHttpCall()
    {
        var calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });
        using var client = CreateClient(handler);

        Assert.Throws<ArgumentException>(() => client.Partition(""));
        Assert.Throws<ArgumentException>(() => client.Partition("   "));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Partition_RejectsTooLong_NoHttpCall()
    {
        var calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });
        using var client = CreateClient(handler);

        Assert.Throws<ArgumentException>(() => client.Partition(new string('a', 257)));
        // 256 is the inclusive upper bound and must be accepted.
        var ok = client.Partition(new string('a', 256));
        Assert.NotNull(ok);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Partition_LastWins_OnRescoping()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.Partition("a").Partition("b").SearchAsync("test");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("partition=b", query);
        Assert.DoesNotContain("partition=a", query);
    }

    [Fact]
    public async Task Partition_DisposingScopedHandle_DoesNotCloseSharedTransport()
    {
        // The scoped clone shares the parent's HttpClient and must not close it on
        // Dispose(); the parent stays fully usable afterward.
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });
        var http = new HttpClient(handler);
        var parent = new AetherClient(http, "http://localhost:9000");

        var scoped = parent.Partition("tenant-x");
        scoped.Dispose();

        // If Dispose() had closed the shared HttpClient, this would throw ObjectDisposedException.
        await parent.SearchAsync("test");
        Assert.Contains("q=test", handler.LastRequest!.RequestUri!.Query);

        parent.Dispose();
    }
}
