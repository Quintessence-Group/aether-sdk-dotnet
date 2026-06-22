using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Contract test for the <see cref="Memory"/> facade, mocked at the same
/// transport layer as the raw-client tests (a routing <see cref="HttpMessageHandler"/>)
/// with the real <see cref="AetherClient"/> underneath, constructed via the DI path
/// (<c>new Memory(entityId, client)</c>).
///
/// This pins the facade to the shipped 0.3.x search surface: hits carry a calibrated
/// <c>score</c> (0–100, higher = better) and a <c>passage</c> — there is no
/// <c>distance</c> field, and <c>retrieve</c> fetches each matched document's text
/// with a follow-up <c>GET /documents/{id}/download</c> (search no longer inlines
/// content).
/// </summary>
public class MemoryTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    // ── routing transport ─────────────────────────────────────────────

    /// <summary>
    /// Records every request and dispatches a scripted response keyed by
    /// <c>(method, path)</c>. A queue value pops one response per call; a single
    /// response is reused. Query strings are stripped for the lookup so routes
    /// stay readable.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<(string Method, string Path), Queue<Func<HttpResponseMessage>>> _routes = new();
        public List<(string Method, string Path, string Query)> Calls { get; } = new();

        public RoutingHandler Route(string method, string path, params Func<HttpResponseMessage>[] responses)
        {
            _routes[(method, path)] = new Queue<Func<HttpResponseMessage>>(responses);
            return this;
        }

        public int CallsTo(string method, string path) =>
            Calls.Count(c => c.Method == method && c.Path == path);

        public (string Method, string Path, string Query) FirstCall => Calls[0];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.Method.Method;
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Calls.Add((method, path, query));

            if (_routes.TryGetValue((method, path), out var queue) && queue.Count > 0)
            {
                var factory = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
                return Task.FromResult(factory());
            }

            throw new InvalidOperationException($"unexpected request: {method} {path}");
        }
    }

    private static Func<HttpResponseMessage> Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        () => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static Func<HttpResponseMessage> Bytes(string text) =>
        () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)),
        };

    private static object InsertResp(string docId = "doc-new", string createdAt = "2026-06-15T00:00:00Z", string entityId = "user-42") =>
        new { doc_id = docId, cid = "cid-1", chunks = 1, vectors = 1, version = 1, created_at = createdAt, entity_id = entityId };

    private static object SearchResp(params object[] hits) =>
        new { query = "q", results = hits };

    private static object Hit(string docId, int score, string passage = "p") =>
        new { doc_id = docId, score, content_type = "text/plain", passage };

    private static object DocResp(string docId, string? createdAt) =>
        new { doc_id = docId, cid = "c", content_type = "text/plain", size_bytes = 1, version = 1, created_at = createdAt, entity_id = "user-42" };

    private static object ListResp(params object[] documents) =>
        new { documents, total = documents.Length, has_more = false };

    private static Memory SyncMemory(RoutingHandler handler, string entityId = "user-42", MemoryOptions? options = null)
    {
        options ??= new MemoryOptions();
        options.Clock = () => FixedNow;
        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        return new Memory(entityId, client, options);
    }

    // ── scoping ───────────────────────────────────────────────────────

    [Fact]
    public async Task Remember_SendsEntityIdField()
    {
        var h = new RoutingHandler().Route("POST", "/documents", Json(InsertResp()));
        await SyncMemory(h).RememberAsync("hello");
        Assert.Equal("POST", h.FirstCall.Method);
        Assert.Contains("entity_id=user-42", h.FirstCall.Query);
    }

    [Fact]
    public async Task Recall_SendsEntityIdFilter()
    {
        var h = new RoutingHandler().Route("GET", "/search", Json(SearchResp()));
        await SyncMemory(h).RecallAsync("anxiety");
        Assert.Contains("entity_id=user-42", h.FirstCall.Query);
    }

    [Fact]
    public async Task List_SendsEntityIdFilter()
    {
        var h = new RoutingHandler().Route("GET", "/documents", Json(ListResp()));
        await SyncMemory(h).ListAsync();
        Assert.Contains("entity_id=user-42", h.FirstCall.Query);
    }

    // ── remember round-trip ───────────────────────────────────────────

    [Fact]
    public async Task Remember_ReturnsMemoryItem()
    {
        var h = new RoutingHandler().Route("POST", "/documents",
            Json(InsertResp(docId: "doc-7", createdAt: "2026-06-15T09:30:00Z")));
        var item = await SyncMemory(h).RememberAsync("anxious about flying");
        Assert.Equal("doc-7", item.Id);
        Assert.Equal("anxious about flying", item.Text);
        Assert.Equal("user-42", item.EntityId);
        Assert.Null(item.Score);
        Assert.Equal("2026-06-15T09:30:00Z", item.CreatedAt);
    }

    [Fact]
    public async Task Remember_EmptyTextIsClientSideError()
    {
        var h = new RoutingHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => SyncMemory(h).RememberAsync("   "));
        Assert.Empty(h.Calls);
    }

    // ── metadata → tags (write-only) ──────────────────────────────────

    [Fact]
    public async Task Metadata_EncodedAsTags()
    {
        var h = new RoutingHandler().Route("POST", "/documents", Json(InsertResp()));
        await SyncMemory(h).RememberAsync("breathing helps",
            new Dictionary<string, string> { ["topic"] = "anxiety" });
        Assert.Contains("tags=topic%3Aanxiety", h.FirstCall.Query);
    }

    [Fact]
    public async Task Metadata_MultipleSortedByKey()
    {
        var h = new RoutingHandler().Route("POST", "/documents", Json(InsertResp()));
        await SyncMemory(h).RememberAsync("x",
            new Dictionary<string, string> { ["topic"] = "anxiety", ["score"] = "5", ["active"] = "yes" });
        Assert.Contains("tags=active%3Ayes%2Cscore%3A5%2Ctopic%3Aanxiety", h.FirstCall.Query);
    }

    [Fact]
    public async Task Metadata_PrefixKeysSortedByKeyNotTag()
    {
        var h = new RoutingHandler().Route("POST", "/documents", Json(InsertResp()));
        await SyncMemory(h).RememberAsync("x",
            new Dictionary<string, string> { ["a0"] = "w", ["a"] = "v" });
        Assert.Contains("tags=a%3Av%2Ca0%3Aw", h.FirstCall.Query);
    }

    [Fact]
    public async Task Metadata_ValueWithFirstColonSplit()
    {
        var h = new RoutingHandler().Route("POST", "/documents", Json(InsertResp()));
        await SyncMemory(h).RememberAsync("x",
            new Dictionary<string, string> { ["time"] = "12:30" });
        Assert.Contains("tags=time%3A12%3A30", h.FirstCall.Query);
    }

    public static IEnumerable<object[]> BadMetadata()
    {
        yield return new object[] { new Dictionary<string, string> { ["topic"] = "a,b" } };
        yield return new object[] { new Dictionary<string, string> { [""] = "v" } };
        yield return new object[] { new Dictionary<string, string> { ["a,b"] = "v" } };
        yield return new object[] { new Dictionary<string, string> { ["a:b"] = "v" } };
    }

    [Theory]
    [MemberData(nameof(BadMetadata))]
    public async Task Metadata_BadRaisesNoHttp(Dictionary<string, string> metadata)
    {
        var h = new RoutingHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => SyncMemory(h).RememberAsync("x", metadata));
        Assert.Empty(h.Calls);
    }

    // ── recall (default: recencyWeight=0) ─────────────────────────────

    [Fact]
    public async Task Recall_SearchThenDownloadServerOrder()
    {
        var h = new RoutingHandler()
            .Route("GET", "/search", Json(SearchResp(Hit("d1", 95), Hit("d2", 70))))
            .Route("GET", "/documents/d1/download", Bytes("first"))
            .Route("GET", "/documents/d2/download", Bytes("second"));

        var items = (await SyncMemory(h).RecallAsync("query", k: 5)).ToList();

        Assert.Equal(1, h.CallsTo("GET", "/search"));
        Assert.Equal(1, h.CallsTo("GET", "/documents/d1/download"));
        Assert.Equal(1, h.CallsTo("GET", "/documents/d2/download"));
        Assert.Equal(new[] { "d1", "d2" }, items.Select(i => i.Id));
        Assert.Equal(new[] { "first", "second" }, items.Select(i => i.Text));
        Assert.All(items, i => Assert.Null(i.CreatedAt));
        // score normalized from the 0–100 wire score; higher = better
        Assert.Equal(0.95, items[0].Score!.Value, 6);
        Assert.Equal(0.70, items[1].Score!.Value, 6);
        // no removed include_content flag; entity filter + k forwarded
        var query = h.FirstCall.Query;
        Assert.DoesNotContain("include_content", query);
        Assert.Contains("entity_id=user-42", query);
        Assert.Contains("k=5", query);
    }

    [Fact]
    public async Task Recall_EmptyQueryIsClientSideError()
    {
        var h = new RoutingHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => SyncMemory(h).RecallAsync("   "));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Recall_KBelowOneIsClientSideError()
    {
        var h = new RoutingHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SyncMemory(h).RecallAsync("query", k: 0));
        Assert.Empty(h.Calls);
    }

    // ── recall (recencyWeight>0: blended re-ranking) ──────────────────
    //
    // recencyWeight=0.5, halfLife=30d, now=2026-06-15. similarity = score/100,
    // recency = 0.5 ** (ageDays / 30). blended = 0.5*sim + 0.5*recency:
    //   docA score=90  age=0d   -> 0.5*0.90 + 0.5*1.0 = 0.95
    //   docB score=80  age=30d  -> 0.5*0.80 + 0.5*0.5 = 0.65
    //   docC score=100 created=null (recency 0) -> 0.5*1.00 + 0.5*0.0 = 0.50
    // Pure score order is [docC, docA, docB]; recency reorders to [docA, docB, docC].

    private static RoutingHandler RecencyHandler() => new RoutingHandler()
        .Route("GET", "/search", Json(SearchResp(Hit("docA", 90), Hit("docB", 80), Hit("docC", 100))))
        .Route("GET", "/documents/docA/download", Bytes("A"))
        .Route("GET", "/documents/docB/download", Bytes("B"))
        .Route("GET", "/documents/docC/download", Bytes("C"))
        .Route("GET", "/documents/docA", Json(DocResp("docA", "2026-06-15T00:00:00Z")))
        .Route("GET", "/documents/docB", Json(DocResp("docB", "2026-05-16T00:00:00Z")))
        .Route("GET", "/documents/docC", Json(DocResp("docC", null)));

    [Fact]
    public async Task Recall_BlendedReorder()
    {
        var items = (await SyncMemory(RecencyHandler()).RecallAsync("q", k: 5, recencyWeight: 0.5)).ToList();
        Assert.Equal(new[] { "docA", "docB", "docC" }, items.Select(i => i.Id));
        Assert.Equal(0.95, items[0].Score!.Value, 6);
        Assert.Equal(0.65, items[1].Score!.Value, 6);
        Assert.Equal(0.50, items[2].Score!.Value, 6);
        // recency mode resolves created_at, so it is populated
        Assert.Equal("2026-06-15T00:00:00Z", items[0].CreatedAt);
    }

    [Fact]
    public async Task Recall_TopKTruncation()
    {
        var items = (await SyncMemory(RecencyHandler()).RecallAsync("q", k: 2, recencyWeight: 0.5)).ToList();
        Assert.Equal(new[] { "docA", "docB" }, items.Select(i => i.Id));
    }

    // ── list (chronological) ──────────────────────────────────────────

    [Fact]
    public async Task List_NewestFirstTextDownloadedScoreNull()
    {
        var h = new RoutingHandler()
            .Route("GET", "/documents", Json(ListResp(
                new { doc_id = "m1", content_type = "text/plain", created_at = "2026-06-15T00:00:00Z" },
                new { doc_id = "m2", content_type = "text/plain", created_at = "2026-06-01T00:00:00Z" })))
            .Route("GET", "/documents/m1/download", Bytes("newest"))
            .Route("GET", "/documents/m2/download", Bytes("older"));

        var items = (await SyncMemory(h).ListAsync()).ToList();
        Assert.Equal(new[] { "m1", "m2" }, items.Select(i => i.Id));
        Assert.Equal(new[] { "newest", "older" }, items.Select(i => i.Text));
        Assert.All(items, i => Assert.Null(i.Score));
    }

    // ── forget ────────────────────────────────────────────────────────

    [Fact]
    public async Task Forget_IssuesOneDelete()
    {
        var h = new RoutingHandler().Route("DELETE", "/documents/doc-x", Json(new { }));
        await SyncMemory(h).ForgetAsync("doc-x");
        Assert.Equal(1, h.CallsTo("DELETE", "/documents/doc-x"));
    }

    [Fact]
    public async Task Forget_EmptyIdRaises()
    {
        var h = new RoutingHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => SyncMemory(h).ForgetAsync(""));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task ForgetAll_DeletesEveryListedAndReturnsCount()
    {
        var h = new RoutingHandler()
            .Route("GET", "/documents",
                Json(ListResp(
                    new { doc_id = "a", content_type = "text/plain" },
                    new { doc_id = "b", content_type = "text/plain" })),
                Json(ListResp()))
            .Route("DELETE", "/documents/a", Json(new { }))
            .Route("DELETE", "/documents/b", Json(new { }));

        var deleted = await SyncMemory(h).ForgetAllAsync();
        Assert.Equal(2, deleted);
        Assert.Equal(1, h.CallsTo("DELETE", "/documents/a"));
        Assert.Equal(1, h.CallsTo("DELETE", "/documents/b"));
    }

    // ── error passthrough ─────────────────────────────────────────────

    [Fact]
    public async Task CreditExhausted_SurfacesTypedError()
    {
        var h = new RoutingHandler().Route("POST", "/documents",
            Json(new { error = "out of credit", code = "credit_exhausted" }, (HttpStatusCode)402));
        await Assert.ThrowsAsync<CreditExhaustedException>(() => SyncMemory(h).RememberAsync("x"));
    }

    // ── invalid construction ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyOrWhitespaceEntityIdRaises(string entityId)
    {
        using var client = new AetherClient(new HttpClient(new RoutingHandler()), "http://localhost:9000");
        Assert.Throws<ArgumentException>(() => new Memory(entityId, client));
    }

    [Fact]
    public void OversizedEntityIdRaises()
    {
        using var client = new AetherClient(new HttpClient(new RoutingHandler()), "http://localhost:9000");
        Assert.Throws<ArgumentException>(() => new Memory(new string('x', 257), client));
    }

    [Fact]
    public void MaxLengthEntityIdOk()
    {
        using var client = new AetherClient(new HttpClient(new RoutingHandler()), "http://localhost:9000");
        var mem = new Memory(new string('x', 256), client);
        Assert.Equal(new string('x', 256), mem.EntityId);
    }

    [Fact]
    public void NullClientRaises()
    {
        Assert.Throws<ArgumentNullException>(() => new Memory("u", (AetherClient)null!));
    }
}
