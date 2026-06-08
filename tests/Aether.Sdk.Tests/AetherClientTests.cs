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

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_handler(request));
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
        Assert.Contains("/documents/abc-123", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(2, result.Version);
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

        Assert.Contains("/documents/abc-123", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Test Doc", doc.Title);
        Assert.Equal(1024, doc.SizeBytes);
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

    // ── Delete ────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SendsDeleteRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", doc_id = "abc-123" });

        using var client = CreateClient(handler);
        await client.DeleteAsync("abc-123");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("/documents/abc-123", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    // ── Restore ───────────────────────────────────────────────────

    [Fact]
    public async Task Restore_PostsToRestore()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "restored", doc_id = "abc-123" });

        using var client = CreateClient(handler);
        await client.RestoreAsync("abc-123");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("/documents/abc-123/restore", handler.LastRequest.RequestUri!.AbsolutePath);
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
                new { doc_id = "abc", distance = 0.15, title = "ML Intro", content_type = "text/plain" },
            },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("machine learning", 5);

        Assert.Contains("q=machine%20learning", handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("k=5", handler.LastRequest.RequestUri.Query);
        Assert.Single(results);
        Assert.Equal(0.15, results[0].Distance, precision: 2);
    }

    [Fact]
    public async Task Search_DefaultsKTo10()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "test", results = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.SearchAsync("test");

        Assert.Contains("k=10", handler.LastRequest!.RequestUri!.Query);
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
        Assert.Contains("/documents/batch", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task BatchInsertAsync_SendsTagsAsCommaJoinedString()
    {
        // Capture the request body up front: the handler buffers content into a
        // rebuilt ByteArrayContent, but we read it synchronously to be safe.
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1 } },
                    }),
                    Encoding.UTF8, "application/json"),
            };
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello", Tags = new List<string> { "x", "y" } },
        });

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var tags = doc.RootElement.GetProperty("documents")[0].GetProperty("tags");
        // Must be a comma-joined STRING, not a JSON array (prod rejects the array with 422).
        Assert.Equal(JsonValueKind.String, tags.ValueKind);
        Assert.Equal("x,y", tags.GetString());
    }

    [Fact]
    public async Task BatchInsertAsync_OmitsTagsWhenNone()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        results = new[] { new { doc_id = "b1", cid = "c1", chunks = 1, vectors = 1, version = 1 } },
                    }),
                    Encoding.UTF8, "application/json"),
            };
        });
        var client = CreateClient(handler);

        await client.BatchInsertAsync(new List<BatchInsertItem>
        {
            new() { Filename = "a.txt", Content = "hello" },
        });

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var item = doc.RootElement.GetProperty("documents")[0];
        Assert.False(item.TryGetProperty("tags", out _));
    }

    [Fact]
    public async Task BatchSearchAsync_SendsTagsAsCommaJoinedString()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        results = new[] { new { query = "test", results = Array.Empty<object>() } },
                    }),
                    Encoding.UTF8, "application/json"),
            };
        });
        var client = CreateClient(handler);

        await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5, Tags = new List<string> { "alpha", "beta" } },
        });

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var tags = doc.RootElement.GetProperty("queries")[0].GetProperty("tags");
        Assert.Equal(JsonValueKind.String, tags.ValueKind);
        Assert.Equal("alpha,beta", tags.GetString());
    }

    [Fact]
    public async Task BatchSearchAsync_SendsCorrectRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new[] { new { query = "test", results = new[] { new { doc_id = "a", distance = 0.1, content_type = "text/plain" } } } },
        });
        var client = CreateClient(handler);

        var results = await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "test", K = 5 },
        });

        Assert.Single(results);
        Assert.Equal("test", results[0].Query);
    }

    // ── Async job operations ─────────────────────────────────────────

    [Fact]
    public async Task EnqueueDocumentAsync_SendsCorrectRequest()
    {
        var handler = MockHttpMessageHandler.WithJson(new { job_id = "j1", status = "pending", poll_url = "/documents/jobs/j1" });
        var client = CreateClient(handler);

        var result = await client.EnqueueDocumentAsync(new byte[] { 1, 2, 3 }, "test.bin");

        Assert.Equal("j1", result.JobId);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/documents/async", handler.LastRequest!.RequestUri!.ToString());
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
}
