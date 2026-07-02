using System.Net;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Usage-feedback capture: the response-level <c>query_id</c> is stamped onto
/// every search hit (tolerant when absent), and <c>SendSearchFeedbackAsync</c>
/// posts the wire body and maps the server's 404/400 rejections. Each test
/// drives a real client over the mocked transport so the genuine request /
/// parse / error-mapping path runs.
/// </summary>
public class SearchFeedbackTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    private static object Hit(string docId = "doc-1") => new
    {
        doc_id = docId,
        score = 90,
        content_type = "text/plain",
    };

    // ── query_id on search results ───────────────────────────────────

    [Fact]
    public async Task SearchAsync_StampsQueryIdOnEveryHit()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "q",
            query_id = "11111111-2222-3333-4444-555555555555",
            results = new[] { Hit(), Hit("doc-2") },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("q");

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
            Assert.Equal("11111111-2222-3333-4444-555555555555", r.QueryId));
    }

    [Fact]
    public async Task SearchAsync_QueryIdNullWhenAbsent()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "q",
            results = new[] { Hit() },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchAsync("q");

        Assert.Single(results);
        Assert.Null(results[0].QueryId);
        Assert.Equal("doc-1", results[0].DocId);
    }

    [Fact]
    public async Task SearchByVectorAsync_StampsQueryId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "",
            query_id = "qid-embed",
            results = new[] { Hit() },
        });

        using var client = CreateClient(handler);
        var results = await client.SearchByVectorAsync(new[] { 0.1f, 0.2f });

        Assert.Equal("qid-embed", results[0].QueryId);
    }

    [Fact]
    public async Task BatchSearchAsync_StampsPerQueryQueryId()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            results = new object[]
            {
                new { query = "a", query_id = "qid-a", results = new[] { Hit() } },
                new { query = "b", results = new[] { Hit("doc-2") } },
            },
        });

        using var client = CreateClient(handler);
        var responses = await client.BatchSearchAsync(new List<BatchSearchQuery>
        {
            new() { Q = "a" },
            new() { Q = "b" },
        });

        Assert.Equal(2, responses.Count);
        Assert.Equal("qid-a", responses[0].Results[0].QueryId);
        Assert.Null(responses[1].Results[0].QueryId);
    }

    // ── SendSearchFeedbackAsync ──────────────────────────────────────

    [Fact]
    public async Task SendSearchFeedbackAsync_PostsPathAndBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new { recorded = true });

        using var client = CreateClient(handler);
        await client.SendSearchFeedbackAsync("qid-1", "doc-1", "used");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/search/feedback", handler.LastRequest.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("qid-1", body.RootElement.GetProperty("query_id").GetString());
        Assert.Equal("doc-1", body.RootElement.GetProperty("doc_id").GetString());
        Assert.Equal("used", body.RootElement.GetProperty("signal").GetString());
    }

    [Fact]
    public async Task SendSearchFeedbackAsync_Maps404OnUnknownQueryId()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "unknown query_id" }, HttpStatusCode.NotFound);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.SendSearchFeedbackAsync("nope", "doc-1", "cited"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task SendSearchFeedbackAsync_Maps400OnInvalidSignal()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "invalid signal", code = "invalid_input" },
            HttpStatusCode.BadRequest);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.SendSearchFeedbackAsync("qid-1", "doc-1", "loved"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("invalid_input", ex.ErrorCode);
    }

    [Theory]
    [InlineData("", "doc-1", "used")]
    [InlineData("qid-1", "", "used")]
    [InlineData("qid-1", "doc-1", "")]
    public async Task SendSearchFeedbackAsync_ValidatesArguments(
        string queryId, string docId, string signal)
    {
        var handler = MockHttpMessageHandler.WithJson(new { recorded = true });

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SendSearchFeedbackAsync(queryId, docId, signal));

        Assert.Null(handler.LastRequest);
    }
}
