using System.Net;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Document summaries (<see cref="AetherClient.GetSummaryAsync"/>,
/// <see cref="AetherClient.PutSummaryAsync"/>, <see cref="AetherClient.RegenerateSummaryAsync"/>)
/// and the <c>summarize</c> ingest option, mocked at the transport layer.
/// </summary>
public class DocumentSummaryTests
{
    private const string PartitionHandle = "scoped-ws_42";
    private const string Revision = "r3f9a1c2b7d4e5f60";

    private static AetherClient Client(WireRecorder wire) =>
        new(new HttpClient(wire), "http://localhost:9000", TimeSpan.FromMilliseconds(1));

    private static WireRecorder Ok(object? body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(() => WireRecorder.Json(body, status));

    private static Dictionary<string, object?> ReadySummary() => new()
    {
        ["doc_id"] = "doc-1",
        ["state"] = "ready",
        ["reason"] = null,
        ["error"] = null,
        ["summary_doc_id"] = "sum-1",
        ["content"] = "## Summary\n- Master services agreement.\n",
        ["revision"] = Revision,
        ["authored_by"] = "aether",
        ["generator"] = "aether:model-x:2026-10-10.v1",
        ["prompt_version"] = "2026-10-10.v1",
        ["generated_at"] = "2026-10-10T09:00:00Z",
        ["source_cid"] = "cid-1",
        ["stale"] = true,
        ["truncated_input"] = true,
    };

    private static Dictionary<string, object?> NoneSummary() => new()
    {
        ["doc_id"] = "doc-1",
        ["state"] = "none",
        ["reason"] = null,
        ["error"] = null,
        ["summary_doc_id"] = null,
        ["content"] = null,
        ["revision"] = null,
        ["authored_by"] = null,
        ["generator"] = null,
        ["prompt_version"] = null,
        ["generated_at"] = null,
        ["source_cid"] = null,
        ["stale"] = false,
        ["truncated_input"] = false,
    };

    private static object Record(string? summary = null) => new Dictionary<string, object?>
    {
        ["doc_id"] = "doc-1",
        ["cid"] = "cid-1",
        ["content_type"] = "text/plain",
        ["size_bytes"] = 5,
        ["chunks"] = 1,
        ["vectors"] = 1,
        ["version"] = 1,
        ["summary"] = summary,
    };

    // ── Summaries ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_ParsesAReadySummary()
    {
        var wire = Ok(ReadySummary());
        var summary = await Client(wire).GetSummaryAsync("doc-1");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/v1/documents/doc-1/summary", sent.Uri.AbsolutePath);
        Assert.Empty(WireRecorder.Query(sent.Uri));
        Assert.Null(sent.Header("Idempotency-Key"));

        Assert.Equal("doc-1", summary.DocId);
        Assert.Equal("ready", summary.State);
        Assert.Null(summary.Reason);
        Assert.Null(summary.Error);
        Assert.Equal("sum-1", summary.SummaryDocId);
        Assert.Equal("## Summary\n- Master services agreement.\n", summary.Content);
        Assert.Equal(Revision, summary.Revision);
        Assert.Equal("aether", summary.AuthoredBy);
        Assert.Equal("aether:model-x:2026-10-10.v1", summary.Generator);
        Assert.Equal("2026-10-10.v1", summary.PromptVersion);
        Assert.Equal("2026-10-10T09:00:00Z", summary.GeneratedAt);
        Assert.Equal("cid-1", summary.SourceCid);
        Assert.True(summary.Stale);
        Assert.True(summary.TruncatedInput);
    }

    [Fact]
    public async Task GetSummary_ParsesANoneSummaryWithNulls()
    {
        var summary = await Client(Ok(NoneSummary())).GetSummaryAsync("doc-1");
        Assert.Equal("none", summary.State);
        Assert.Null(summary.Content);
        Assert.Null(summary.Revision);
        Assert.Null(summary.AuthoredBy);
        Assert.Null(summary.SourceCid);
        Assert.False(summary.Stale);
        Assert.False(summary.TruncatedInput);
    }

    [Fact]
    public async Task GetSummary_ToleratesAMinimalBody()
    {
        var summary = await Client(Ok(new { doc_id = "doc-1", state = "skipped", reason = "below_min_chars" }))
            .GetSummaryAsync("doc-1");
        Assert.Equal("skipped", summary.State);
        Assert.Equal("below_min_chars", summary.Reason);
        Assert.False(summary.Stale);
    }

    [Fact]
    public async Task Summaries_SendThePartitionGuard()
    {
        var wire = Ok(ReadySummary());
        var scoped = Client(wire).Partition(PartitionHandle);
        await scoped.GetSummaryAsync("doc-1");
        await scoped.PutSummaryAsync("doc-1", "mine");
        await scoped.RegenerateSummaryAsync("doc-1", force: true);

        Assert.All(wire.Requests, sent =>
            Assert.Equal(PartitionHandle, WireRecorder.Query(sent.Uri)["partition"]));
        Assert.Equal("true", WireRecorder.Query(wire.Requests[2].Uri)["force"]);
    }

    [Fact]
    public async Task PutSummary_SendsJsonContent()
    {
        var created = ReadySummary();
        created["authored_by"] = "user";
        created["stale"] = false;
        var wire = Ok(created, HttpStatusCode.Created);
        var summary = await Client(wire).PutSummaryAsync("doc-1", "## Summary\n- Mine.\n");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/v1/documents/doc-1/summary", sent.Uri.AbsolutePath);
        Assert.StartsWith("application/json", sent.ContentType);
        WireRecorder.AssertJson("""{"content":"## Summary\n- Mine.\n"}""", sent.Body);
        Assert.Null(sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
        Assert.Null(sent.Header("Idempotency-Key"));
        Assert.Equal("user", summary.AuthoredBy);
        Assert.Equal("ready", summary.State);
    }

    [Fact]
    public async Task PutSummary_ExpectedRevisionSendsQuotedIfMatch()
    {
        var wire = Ok(ReadySummary());
        await Client(wire).PutSummaryAsync("doc-1", "mine", expectedRevision: Revision);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("\"r3f9a1c2b7d4e5f60\"", sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
    }

    [Fact]
    public async Task PutSummary_RevisionConflictIsTheBaseApiError()
    {
        var wire = new WireRecorder(() => WireRecorder.Json(
            new { error = "revision conflict", code = "summary_revision_conflict", current_revision = "r9" },
            (HttpStatusCode)412));
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => Client(wire).PutSummaryAsync("doc-1", "mine", expectedRevision: "r1"));
        Assert.Equal((HttpStatusCode)412, ex.StatusCode);
        Assert.Equal("summary_revision_conflict", ex.ErrorCode);
    }

    [Fact]
    public async Task RegenerateSummary_ForcePostsAndParsesPending()
    {
        var pending = ReadySummary();
        pending["state"] = "pending";
        var wire = Ok(pending, HttpStatusCode.Accepted);
        var summary = await Client(wire).RegenerateSummaryAsync("doc-1", force: true);

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/v1/documents/doc-1/summary", sent.Uri.AbsolutePath);
        Assert.Equal(new Dictionary<string, string> { ["force"] = "true" }, WireRecorder.Query(sent.Uri));
        Assert.Equal("pending", summary.State);
        Assert.Equal("## Summary\n- Master services agreement.\n", summary.Content);
    }

    [Fact]
    public async Task RegenerateSummary_WithoutForceSendsNoForce()
    {
        var wire = Ok(NoneSummary(), HttpStatusCode.Accepted);
        await Client(wire).RegenerateSummaryAsync("doc-1");
        Assert.Empty(WireRecorder.Query(Assert.Single(wire.Requests).Uri));
    }

    [Fact]
    public async Task RegenerateSummary_UserAuthoredIsTheBaseApiError()
    {
        var wire = new WireRecorder(() => WireRecorder.Json(
            new { error = "user-authored", code = "summary_user_authored" }, HttpStatusCode.Conflict));
        var ex = await Assert.ThrowsAsync<AetherApiException>(() => Client(wire).RegenerateSummaryAsync("doc-1"));
        Assert.Equal("summary_user_authored", ex.ErrorCode);
    }

    [Fact]
    public async Task Summaries_RejectBadArgumentsWithoutHttp()
    {
        var wire = Ok(ReadySummary());
        var client = Client(wire);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetSummaryAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.PutSummaryAsync("", "mine"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RegenerateSummaryAsync(""));
        var revision = await Assert.ThrowsAsync<ArgumentException>(
            () => client.PutSummaryAsync("doc-1", "mine", expectedRevision: ""));
        Assert.Equal("expectedRevision", revision.ParamName);
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task FeatureNotEnabled_IsTheBaseApiError()
    {
        var wire = new WireRecorder(() => WireRecorder.Json(
            new { error = "summaries are not enabled", code = "feature_not_enabled", feature = "summaries" },
            HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<AetherApiException>(() => Client(wire).GetSummaryAsync("doc-1"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("feature_not_enabled", ex.ErrorCode);
    }

    // ── summarize at ingest ─────────────────────────────────────────────

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(null, null)]
    public async Task InsertText_SummarizeIsTriState(bool? summarize, string? expected)
    {
        var wire = Ok(Record(summarize == true ? "pending" : null));
        var record = await Client(wire).InsertTextAsync("hello", summarize: summarize);

        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        if (expected == null)
            Assert.False(query.ContainsKey("summarize"));
        else
            Assert.Equal(expected, query["summarize"]);
        Assert.Equal(summarize == true ? "pending" : null, record.Summary);
    }

    [Fact]
    public async Task Insert_ForwardsSummarizeAndParsesSkipped()
    {
        var wire = Ok(Record("skipped"));
        var record = await Client(wire).Partition(PartitionHandle)
            .InsertAsync(new byte[] { 1, 2 }, "a.pdf", summarize: true);
        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("true", query["summarize"]);
        Assert.Equal(PartitionHandle, query["partition"]);
        Assert.Equal("skipped", record.Summary);
    }

    [Fact]
    public async Task InsertStream_ForwardsSummarize()
    {
        var wire = Ok(Record("pending"));
        await Client(wire).InsertStreamAsync(new MemoryStream(new byte[] { 1 }), "a.bin", summarize: false);
        Assert.Equal("false", WireRecorder.Query(Assert.Single(wire.Requests).Uri)["summarize"]);
    }

    [Fact]
    public async Task EnqueueDocument_ForwardsSummarize()
    {
        var wire = Ok(new { job_id = "job-1", status = "queued", poll_url = "/v1/documents/jobs/job-1" });
        await Client(wire).EnqueueDocumentAsync(new byte[] { 1 }, "a.txt", summarize: true);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("/v1/documents/async", sent.Uri.AbsolutePath);
        Assert.Equal("true", WireRecorder.Query(sent.Uri)["summarize"]);
    }

    [Fact]
    public async Task WaitForJob_ParsesSummaryState()
    {
        var wire = Ok(new { status = "completed", doc_id = "doc-1", summary_state = "pending" });
        var status = await Client(wire).WaitForJobAsync("job-1");
        Assert.Equal("pending", status.SummaryState);

        var plain = Ok(new { status = "completed", doc_id = "doc-1" });
        Assert.Null((await Client(plain).WaitForJobAsync("job-1")).SummaryState);
    }

    [Fact]
    public async Task BatchInsert_SendsBatchAndItemSummarize()
    {
        var wire = Ok(new { results = new[] { Record("pending"), Record(null) } });
        var records = await Client(wire).BatchInsertAsync(
            new List<BatchInsertItem>
            {
                new() { Filename = "a.txt", Content = "a" },
                new() { Filename = "b.txt", Content = "b", Summarize = false },
            },
            summarize: true);

        var body = JsonDocument.Parse(Assert.Single(wire.Requests).Body!).RootElement;
        Assert.True(body.GetProperty("summarize").GetBoolean());
        var items = body.GetProperty("documents");
        Assert.False(items[0].TryGetProperty("summarize", out _));
        Assert.False(items[1].GetProperty("summarize").GetBoolean());
        Assert.Equal("pending", records[0].Summary);
        Assert.Null(records[1].Summary);
    }

    [Fact]
    public async Task BatchInsert_OmitsSummarizeWhenUnset()
    {
        var wire = Ok(new { results = new[] { Record() } });
        await Client(wire).BatchInsertAsync(new List<BatchInsertItem> { new() { Filename = "a.txt", Content = "a" } });
        var body = JsonDocument.Parse(Assert.Single(wire.Requests).Body!).RootElement;
        Assert.False(body.TryGetProperty("summarize", out _));
        Assert.False(body.GetProperty("documents")[0].TryGetProperty("summarize", out _));
    }

    [Fact]
    public async Task BatchInsert_PartitionHandleKeepsItemSummarize()
    {
        var wire = Ok(new { results = new[] { Record() } });
        await Client(wire).Partition(PartitionHandle).BatchInsertAsync(
            new List<BatchInsertItem> { new() { Filename = "a.txt", Content = "a", Summarize = true } });
        var item = JsonDocument.Parse(Assert.Single(wire.Requests).Body!).RootElement.GetProperty("documents")[0];
        Assert.True(item.GetProperty("summarize").GetBoolean());
        Assert.Equal(PartitionHandle, item.GetProperty("partition").GetString());
    }

    // ── Memory: constructor default + per-call override ─────────────────

    private static async Task<string?> RememberSummarize(
        bool? constructorDefault, Func<Memory, Task> remember)
    {
        var wire = Ok(Record());
        var memory = new Memory("acme", Client(wire), new MemoryOptions { Summarize = constructorDefault });
        await remember(memory);
        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        return query.TryGetValue("summarize", out var value) ? value : null;
    }

    [Fact]
    public async Task Remember_UnsetSendsNothing() =>
        Assert.Null(await RememberSummarize(null, m => m.RememberAsync("fact")));

    [Fact]
    public async Task Remember_ConstructorDefaultApplies()
    {
        Assert.Equal("true", await RememberSummarize(true, m => m.RememberAsync("fact")));
        Assert.Equal("false", await RememberSummarize(false, m => m.RememberAsync("fact")));
    }

    [Fact]
    public async Task Remember_PerCallOverridesTheDefault()
    {
        Assert.Equal("false", await RememberSummarize(true, m => m.RememberAsync("fact", summarize: false)));
        Assert.Equal("true", await RememberSummarize(null, m => m.RememberAsync("fact", summarize: true)));
    }

    [Fact]
    public async Task Remember_StringMetadataOverloadForwardsSummarize()
    {
        var value = await RememberSummarize(null, m => m.RememberAsync(
            "fact", new Dictionary<string, string> { ["topic"] = "terms" }, summarize: true));
        Assert.Equal("true", value);
    }

    [Fact]
    public async Task Remember_SendsExtractAndSummarizeTogether()
    {
        var wire = Ok(Record("pending"));
        var memory = new Memory("acme", Client(wire), new MemoryOptions { Summarize = true });
        var item = await memory.RememberAsync("fact", extract: true);
        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("true", query["extract_facts"]);
        Assert.Equal("true", query["summarize"]);
        Assert.Equal("doc-1", item.Id);
    }
}
