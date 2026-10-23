using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// <see cref="AetherClient.GetTextAsync"/> and the recall/list hydration rule
/// (MEMORY_CONTRACT §25.6, golden table §27.5), mocked at the transport layer
/// with a route-keyed <see cref="HttpMessageHandler"/> so the real client and
/// <see cref="Memory"/> code run end to end.
/// </summary>
public class DocumentTextTests
{
    private const string ParsedText = "# Master Services Agreement\n\nParsed text.";

    private sealed class FakeEngine : HttpMessageHandler
    {
        public object? Search { get; init; }
        public object? Listing { get; init; }
        public HttpStatusCode TextStatus { get; init; } = HttpStatusCode.OK;
        public object? TextBody { get; init; }
        public bool ForbidText { get; init; }
        public List<string> Downloads { get; } = new();
        public List<Uri> Texts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            lock (Texts)
            {
                if (path == "/v1/search")
                    return Task.FromResult(Json(new { query = "q", results = new[] { Search } }));
                if (path == "/v1/documents")
                    return Task.FromResult(Json(new
                    {
                        documents = new[] { Listing },
                        count = 1,
                        total = 1,
                        has_more = false,
                    }));
                if (path.EndsWith("/download", StringComparison.Ordinal))
                {
                    Downloads.Add(path);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes("original text")),
                    });
                }
                if (path.EndsWith("/text", StringComparison.Ordinal))
                {
                    if (ForbidText)
                        throw new InvalidOperationException($"/text must not be called: {uri}");
                    Texts.Add(uri);
                    var body = TextBody ?? (TextStatus == HttpStatusCode.OK
                        ? DocumentTextBody()
                        : new Dictionary<string, object?> { ["error"] = "not found", ["code"] = "document_not_found" });
                    return Task.FromResult(Json(body, TextStatus));
                }
            }
            throw new InvalidOperationException($"unexpected request {request.Method} {uri}");
        }
    }

    private static HttpResponseMessage Json(object? body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static Dictionary<string, object?> DocumentTextBody(
        string state = "ready", string? source = "passages", string text = ParsedText)
    {
        return new Dictionary<string, object?>
        {
            ["doc_id"] = "doc-1",
            ["content_type"] = "application/pdf",
            ["source"] = source,
            ["state"] = state,
            ["text"] = text,
            ["offset"] = 0,
            ["next_offset"] = null,
            ["total_bytes"] = Encoding.UTF8.GetByteCount(text),
            ["truncated"] = false,
            ["section"] = null,
            ["sections"] = null,
        };
    }

    private static Dictionary<string, object?> Hit(string contentType, string? passage = "the matched passage", string? modality = null)
    {
        var hit = new Dictionary<string, object?>
        {
            ["doc_id"] = "doc-1",
            ["score"] = 90,
            ["content_type"] = contentType,
            ["tags"] = Array.Empty<string>(),
            ["metadata"] = new Dictionary<string, object>(),
        };
        if (passage != null) hit["passage"] = passage;
        if (modality != null) hit["modality"] = modality;
        return hit;
    }

    private static Dictionary<string, object?> Record(string contentType, string? modality = null, string? derivedText = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["doc_id"] = "doc-1",
            ["cid"] = "",
            ["content_type"] = contentType,
            ["size_bytes"] = 10,
            ["version"] = 1,
            ["created_at"] = "2026-10-01T00:00:00Z",
            ["entity_id"] = "acme",
        };
        if (modality != null) record["modality"] = modality;
        if (derivedText != null) record["derived_text"] = derivedText;
        return record;
    }

    private static AetherClient Client(FakeEngine engine) =>
        new(new HttpClient(engine), "http://localhost:9000");

    public static readonly TheoryData<string> DownloadRows = new()
    {
        "text/plain",
        "text/markdown; charset=utf-8",
        "TEXT/MARKDOWN",
        "text/x-python",
        "application/json",
        "application/x-yaml",
    };

    public static readonly TheoryData<string> GetTextRows = new()
    {
        "text/html",
        "text/csv",
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/octet-stream",
        "",
    };

    // ── GetTextAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetText_MapsOptionsAndParses()
    {
        var body = DocumentTextBody(text: "## Agreed terms\n- Net 30\n");
        body["next_offset"] = 25;
        body["truncated"] = true;
        body["section"] = new Dictionary<string, object>
        {
            ["heading"] = "Agreed terms",
            ["level"] = 2,
            ["slug"] = "agreed-terms",
            ["start_line"] = 4,
            ["end_line"] = 6,
            ["bytes"] = 71,
        };
        var engine = new FakeEngine { TextBody = body };
        var text = await Client(engine).Partition("client-a")
            .GetTextAsync("doc 1", section: "Agreed terms", offset: 0, maxBytes: 25);

        var uri = engine.Texts.Single();
        Assert.Equal("/v1/documents/doc%201/text", uri.AbsolutePath);
        var query = uri.Query;
        Assert.Contains("section=Agreed%20terms", query);
        Assert.Contains("offset=0", query);
        Assert.Contains("max_bytes=25", query);
        Assert.Contains("partition=client-a", query);
        Assert.DoesNotContain("outline", query);

        Assert.Equal("passages", text.Source);
        Assert.Equal("ready", text.State);
        Assert.Equal(25, text.NextOffset);
        Assert.True(text.Truncated);
        Assert.Equal("agreed-terms", text.Section!.Slug);
        Assert.Equal(4, text.Section.StartLine);
        Assert.Null(text.Sections);
    }

    [Fact]
    public async Task GetText_OutlineParsesSectionsAndNullNextOffset()
    {
        var body = DocumentTextBody(text: "");
        body["sections"] = new[]
        {
            new Dictionary<string, object>
            {
                ["heading"] = "Acme memory",
                ["level"] = 1,
                ["slug"] = "acme-memory",
                ["start_line"] = 1,
                ["end_line"] = 9,
                ["bytes"] = 122,
            },
        };
        var engine = new FakeEngine { TextBody = body };
        var text = await Client(engine).GetTextAsync("doc-1", outline: true);
        Assert.Contains("outline=true", engine.Texts.Single().Query);
        Assert.Null(text.NextOffset);
        Assert.Equal(122, Assert.Single(text.Sections!).Bytes);
    }

    [Fact]
    public async Task GetText_RejectsEmptyDocIdWithoutHttp()
    {
        var engine = new FakeEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => Client(engine).GetTextAsync(""));
        Assert.Empty(engine.Texts);
    }

    [Fact]
    public async Task GetText_SurfacesSectionErrors()
    {
        var engine = new FakeEngine
        {
            TextStatus = HttpStatusCode.Conflict,
            TextBody = new { error = "ambiguous", code = "document_section_ambiguous" },
        };
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => Client(engine).GetTextAsync("doc-1", section: "notes"));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal("document_section_ambiguous", ex.ErrorCode);
    }

    // ── Hydration golden table ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(DownloadRows))]
    public void TextLike_DownloadRows(string contentType) =>
        Assert.True(AetherClient.IsTextLikeContentType(contentType));

    [Theory]
    [MemberData(nameof(GetTextRows))]
    public void TextLike_GetTextRows(string contentType) =>
        Assert.False(AetherClient.IsTextLikeContentType(contentType));

    [Theory]
    [MemberData(nameof(DownloadRows))]
    public async Task Retrieve_DownloadsTextLikeTypes(string contentType)
    {
        var engine = new FakeEngine { Search = Hit(contentType), ForbidText = true };
        var result = Assert.Single(await Client(engine).RetrieveAsync("q"));
        Assert.Equal("original text", result.Content);
        Assert.Single(engine.Downloads);
    }

    [Theory]
    [MemberData(nameof(GetTextRows))]
    public async Task Retrieve_ReadsOtherTypesThroughText(string contentType)
    {
        var engine = new FakeEngine { Search = Hit(contentType) };
        var result = Assert.Single(await Client(engine).RetrieveAsync("q"));
        Assert.Equal(ParsedText, result.Content);
        Assert.Empty(engine.Downloads);
        Assert.Contains("max_bytes=1048576", Assert.Single(engine.Texts).Query);
    }

    [Theory]
    [MemberData(nameof(GetTextRows))]
    public async Task MemoryList_ReadsOtherTypesThroughText(string contentType)
    {
        var engine = new FakeEngine { Listing = Record(contentType) };
        var item = Assert.Single(await new Memory("acme", Client(engine)).ListAsync());
        Assert.Equal(ParsedText, item.Text);
        Assert.Empty(engine.Downloads);
    }

    [Theory]
    [MemberData(nameof(DownloadRows))]
    public async Task MemoryList_DownloadsTextLikeTypes(string contentType)
    {
        var engine = new FakeEngine { Listing = Record(contentType), ForbidText = true };
        var item = Assert.Single(await new Memory("acme", Client(engine)).ListAsync());
        Assert.Equal("original text", item.Text);
    }

    [Fact]
    public async Task Retrieve_UnsupportedFallsBackToPassage()
    {
        var engine = new FakeEngine
        {
            Search = Hit("application/pdf"),
            TextBody = DocumentTextBody(state: "unsupported", source: null, text: ""),
        };
        var result = Assert.Single(await Client(engine).RetrieveAsync("q"));
        Assert.Equal("the matched passage", result.Content);
    }

    [Fact]
    public async Task Retrieve_404FallsBackToPassage()
    {
        var engine = new FakeEngine { Search = Hit("application/pdf"), TextStatus = HttpStatusCode.NotFound };
        var result = Assert.Single(await Client(engine).RetrieveAsync("q"));
        Assert.Equal("the matched passage", result.Content);
        Assert.Empty(engine.Downloads);
    }

    [Fact]
    public async Task Retrieve_MissingPassageFallsBackToEmpty()
    {
        var engine = new FakeEngine { Search = Hit("application/pdf", passage: null), TextStatus = HttpStatusCode.NotFound };
        var result = Assert.Single(await Client(engine).RetrieveAsync("q"));
        Assert.Equal("", result.Content);
    }

    [Fact]
    public async Task Recall_UsesHydratedTextForPdfHit()
    {
        var engine = new FakeEngine { Search = Hit("application/pdf") };
        var item = Assert.Single(await new Memory("acme", Client(engine)).RecallAsync("terms"));
        Assert.Equal(ParsedText, item.Text);
        Assert.Empty(engine.Downloads);
    }

    [Fact]
    public async Task MemoryList_FallsBackToEmpty()
    {
        var unsupported = new FakeEngine
        {
            Listing = Record("application/pdf"),
            TextBody = DocumentTextBody(state: "unsupported", source: null, text: ""),
        };
        Assert.Equal("", Assert.Single(await new Memory("acme", Client(unsupported)).ListAsync()).Text);

        var missing = new FakeEngine { Listing = Record("application/pdf"), TextStatus = HttpStatusCode.NotFound };
        Assert.Equal("", Assert.Single(await new Memory("acme", Client(missing)).ListAsync()).Text);
    }

    [Fact]
    public async Task OtherErrorsPropagate()
    {
        var engine = new FakeEngine
        {
            Search = Hit("application/pdf"),
            TextStatus = HttpStatusCode.InternalServerError,
            TextBody = new { error = "boom" },
        };
        var ex = await Assert.ThrowsAsync<AetherApiException>(() => Client(engine).RetrieveAsync("q"));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Theory]
    [InlineData("image/png", "image")]
    [InlineData("audio/mpeg", "audio")]
    public async Task Media_IsNeverRead(string contentType, string modality)
    {
        var searched = new FakeEngine { Search = Hit(contentType, modality: modality), ForbidText = true };
        var result = Assert.Single(await Client(searched).RetrieveAsync("q"));
        Assert.Equal("the matched passage", result.Content);
        Assert.Empty(searched.Downloads);

        var listed = new FakeEngine
        {
            Listing = Record(contentType, modality: modality, derivedText: "a caption"),
            ForbidText = true,
        };
        var item = Assert.Single(await new Memory("acme", Client(listed)).ListAsync());
        Assert.Equal("a caption", item.Text);
        Assert.Empty(listed.Downloads);
    }
}
