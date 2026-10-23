using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Records every request (method, URI, headers, content type, body) and answers
/// from a callback that sees the request and its zero-based index, so retries and
/// multi-request calls can be asserted at the transport layer.
/// </summary>
internal sealed class WireRecorder : HttpMessageHandler
{
    internal sealed record Sent(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        string? ContentType,
        string? Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    private readonly Func<Sent, int, HttpResponseMessage> _respond;

    public List<Sent> Requests { get; } = new();

    public WireRecorder(Func<Sent, int, HttpResponseMessage> respond) => _respond = respond;

    public WireRecorder(Func<HttpResponseMessage> respond) : this((_, _) => respond()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(
            request.Method, request.RequestUri!, headers,
            request.Content?.Headers.ContentType?.ToString(), body);
        int index;
        lock (Requests)
        {
            index = Requests.Count;
            Requests.Add(sent);
        }
        return _respond(sent, index);
    }

    public static HttpResponseMessage Json(object? body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (etag != null)
            response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    }

    /// <summary>Decoded query parameters of a request URI.</summary>
    public static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                kv => Uri.UnescapeDataString(kv[0]),
                kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "");

    /// <summary>Key-order-independent canonical form of a JSON document.</summary>
    public static string Canonical(string json) => Canonical(JsonDocument.Parse(json).RootElement);

    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText(),
    };

    public static void AssertJson(string expected, string? actual) =>
        Assert.Equal(Canonical(expected), Canonical(actual!));
}

/// <summary>
/// The <see cref="MemoryFiles"/> facade and the cross-entity
/// <see cref="AetherClient.ListMemoryFilesAsync"/> / <see cref="AetherClient.GrepMemoryFilesAsync"/>,
/// mocked at the transport layer.
/// </summary>
public class MemoryFilesTests
{
    private const string Owner = "scoped:ws_42:company:c_7";
    private const string PartitionHandle = "scoped-ws_42";
    private const string AgreedTermsContent = "## Agreed terms\n- 2026-09-01 — Net 30 payment terms (alice, ISS-12)\n\n";
    private const string Name128 =
        "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz" +
        "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwx";

    private static readonly Regex UuidV4 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase);

    // ── Fixtures ────────────────────────────────────────────────────────

    private static AetherClient Client(WireRecorder wire) =>
        new(new HttpClient(wire), "http://localhost:9000", TimeSpan.FromMilliseconds(1));

    /// <summary>The owner's Memory on the shared partition handle.</summary>
    private static Memory Mem(WireRecorder wire) => new(Owner, Client(wire).Partition(PartitionHandle));

    private static Dictionary<string, object?> Section(string heading, int level, string slug, int start, int end, int bytes) =>
        new()
        {
            ["heading"] = heading,
            ["level"] = level,
            ["slug"] = slug,
            ["start_line"] = start,
            ["end_line"] = end,
            ["bytes"] = bytes,
        };

    // Outline of the shared "Acme memory" vector (122 bytes).
    private static object[] VectorASections() => new object[]
    {
        Section("Acme memory", 1, "acme-memory", 1, 9, 122),
        Section("Agreed terms", 2, "agreed-terms", 4, 6, 71),
        Section("Exceptions", 2, "exceptions", 7, 8, 15),
        Section("Notes", 2, "notes", 9, 9, 9),
    };

    private static Dictionary<string, object?> Info(int version = 7) => new()
    {
        ["entity_id"] = Owner,
        ["partition"] = PartitionHandle,
        ["name"] = "memory.md",
        ["file_id"] = "3d1f0c2e-1111-4222-8333-944455556666",
        ["doc_id"] = "a8c2d7e1-1111-4222-8333-944455556666",
        ["version"] = version,
        ["bytes"] = 122,
        ["content_hash"] = "hash-9f00",
        ["author"] = "scoped:ws_42:user:u_3",
        ["principal"] = "key:ak_1a2b3c4d",
        ["op"] = "append_section",
        ["created_at"] = "2026-09-01T10:00:00Z",
        ["updated_at"] = "2026-10-10T09:12:44Z",
    };

    // The documented section read of memory.md (version 7, "Agreed terms").
    private static Dictionary<string, object?> ReadBody()
    {
        var body = Info();
        body["is_current"] = true;
        body["content"] = AgreedTermsContent;
        body["section"] = Section("Agreed terms", 2, "agreed-terms", 4, 6, 71);
        body["offset"] = 0;
        body["next_offset"] = null;
        body["total_bytes"] = 71;
        body["truncated"] = false;
        body["sections"] = VectorASections();
        return body;
    }

    private static Dictionary<string, object?> WriteResult(
        int version = 8,
        int? previousVersion = 7,
        bool fileCreated = false,
        bool unchanged = false,
        object? section = null,
        bool sectionCreated = false)
    {
        var body = Info(version);
        body["unchanged"] = unchanged;
        body["previous_version"] = previousVersion;
        body["file_created"] = fileCreated;
        body["section"] = section;
        body["section_created"] = sectionCreated;
        body["sections"] = VectorASections();
        return body;
    }

    private static WireRecorder Ok(object? body, HttpStatusCode status = HttpStatusCode.OK, string? etag = "\"8\"") =>
        new(() => WireRecorder.Json(body, status, etag));

    private static WireRecorder Error(HttpStatusCode status, object body) =>
        new(() => WireRecorder.Json(body, status));

    // ── Names ───────────────────────────────────────────────────────────

    public static readonly TheoryData<string> AcceptedNames = new()
    {
        "memory.md", "a", "A", "0", "a.", "Grep", "grep.md", "notes_2026-10.md", "x.y.z", Name128,
    };

    public static readonly TheoryData<string> RejectedNames = new()
    {
        "", ".hidden", "-x", "_x", ".", "..", "a..b", "a...b",
        "a/b", "a b", "a\\b", "memory.md ", "naïve.md", "grep", Name128 + "y",
        "memory.md\n",
    };

    [Fact]
    public void Name128_Is128Chars() => Assert.Equal(128, Name128.Length);

    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void ValidateName_AcceptsValidNames(string name) => MemoryFiles.ValidateName(name);

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public async Task ValidateName_RejectsInvalidNames_ValidatorAndReadWithoutHttp(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => MemoryFiles.ValidateName(name));
        Assert.Equal("name", ex.ParamName);
        Assert.Contains("invalid memory file name", ex.Message);

        var wire = Ok(ReadBody());
        await Assert.ThrowsAsync<ArgumentException>(() => Mem(wire).Files.ReadAsync(name));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public void ValidateName_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => MemoryFiles.ValidateName(null!));

    [Fact]
    public async Task EveryPerFileOp_RejectsAnInvalidNameWithoutHttp()
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        const string bad = "a/b";
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync(bad, "x", expectedVersion: 1));
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendAsync(bad, "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendSectionAsync(bad, "Notes", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReplaceSectionAsync(bad, "Notes", "x", create: true));
        await Assert.ThrowsAsync<ArgumentException>(() => files.VersionsAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DiffAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync(bad));
        Assert.Empty(wire.Requests);
    }

    // ── Scoping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_SendsOwnerEntityAndPartition()
    {
        var wire = Ok(ReadBody(), etag: "\"7\"");
        await Mem(wire).Files.ReadAsync("memory.md");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/v1/memory/files/memory.md", sent.Uri.AbsolutePath);
        var query = WireRecorder.Query(sent.Uri);
        Assert.Equal(Owner, query["entity_id"]);
        Assert.Equal(PartitionHandle, query["partition"]);
        Assert.Equal(new[] { "entity_id", "partition" }, query.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Null(sent.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task Write_SendsOwnerEntityAndPartition()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.WriteAsync("memory.md", "# Acme memory\n", expectedVersion: 7);

        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal(Owner, query["entity_id"]);
        Assert.Equal(PartitionHandle, query["partition"]);
    }

    [Fact]
    public async Task List_SendsOwnerEntityAndPartition()
    {
        var wire = Ok(new { files = new[] { Info() }, next_cursor = (string?)null });
        var page = await Mem(wire).Files.ListAsync();

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/v1/memory/files", sent.Uri.AbsolutePath);
        var query = WireRecorder.Query(sent.Uri);
        Assert.Equal(Owner, query["entity_id"]);
        Assert.Equal(PartitionHandle, query["partition"]);
        Assert.False(query.ContainsKey("entity_prefix"));
        Assert.Equal("memory.md", Assert.Single(page.Files).Name);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Grep_PutsOwnerInBodyAndPartitionInQuery()
    {
        var wire = Ok(new { matches = Array.Empty<object>(), truncated = false, files_scanned = 0, bytes_scanned = 0 });
        await Mem(wire).Files.GrepAsync("Net 30");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/v1/memory/files/grep", sent.Uri.AbsolutePath);
        Assert.Equal(
            new Dictionary<string, string> { ["partition"] = PartitionHandle },
            WireRecorder.Query(sent.Uri));
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Assert.Equal(Owner, body.GetProperty("entity_id").GetString());
        Assert.False(body.TryGetProperty("entity_prefix", out _));
    }

    [Fact]
    public async Task Delete_SendsOwnerEntityAndPartition()
    {
        var wire = Ok(new { entity_id = Owner, partition = PartitionHandle, name = "memory.md", file_id = "f", version = 7, hard = false, versions_deleted = 1 });
        await Mem(wire).Files.DeleteAsync("memory.md");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        var query = WireRecorder.Query(sent.Uri);
        Assert.Equal(Owner, query["entity_id"]);
        Assert.Equal(PartitionHandle, query["partition"]);
    }

    [Fact]
    public async Task UnscopedClient_SendsNoPartition()
    {
        var wire = Ok(ReadBody(), etag: "\"7\"");
        await new Memory(Owner, Client(wire)).Files.ReadAsync("memory.md");
        Assert.False(WireRecorder.Query(Assert.Single(wire.Requests).Uri).ContainsKey("partition"));
    }

    [Fact]
    public async Task ListMemoryFiles_SendsEntityPrefixOnly()
    {
        var wire = Ok(new { files = Array.Empty<object>(), next_cursor = "c2" });
        var page = await Client(wire).Partition(PartitionHandle)
            .ListMemoryFilesAsync(entityPrefix: "scoped:ws_42:company:");

        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("scoped:ws_42:company:", query["entity_prefix"]);
        Assert.False(query.ContainsKey("entity_id"));
        Assert.Equal(PartitionHandle, query["partition"]);
        Assert.Equal("c2", page.NextCursor);
    }

    [Fact]
    public async Task ListMemoryFiles_WithoutScopeListsEverythingInScope()
    {
        var wire = Ok(new { files = Array.Empty<object>(), next_cursor = (string?)null });
        await Client(wire).ListMemoryFilesAsync(entityId: "", entityPrefix: "", prefix: "", cursor: "");
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("/v1/memory/files", sent.Uri.AbsolutePath);
        Assert.Empty(WireRecorder.Query(sent.Uri));
    }

    [Fact]
    public async Task GrepMemoryFiles_PutsEntityPrefixInBody()
    {
        var wire = Ok(new { matches = Array.Empty<object>(), truncated = false, files_scanned = 3, bytes_scanned = 300 });
        await Client(wire).GrepMemoryFilesAsync("Net 30", entityPrefix: "scoped:ws_42:company:");

        var body = JsonDocument.Parse(Assert.Single(wire.Requests).Body!).RootElement;
        Assert.Equal("scoped:ws_42:company:", body.GetProperty("entity_prefix").GetString());
        Assert.False(body.TryGetProperty("entity_id", out _));
    }

    [Fact]
    public async Task CrossEntity_BothEntityIdAndPrefixRejectedWithoutHttp()
    {
        var wire = Ok(new { });
        var client = Client(wire);
        var list = await Assert.ThrowsAsync<ArgumentException>(
            () => client.ListMemoryFilesAsync(entityId: Owner, entityPrefix: "scoped:ws_42:"));
        Assert.Contains("mutually exclusive", list.Message);
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GrepMemoryFilesAsync("x", entityId: Owner, entityPrefix: "scoped:ws_42:"));
        Assert.Empty(wire.Requests);
    }

    // ── Preconditions ───────────────────────────────────────────────────

    [Theory]
    [InlineData(7, "\"7\"")]
    [InlineData(1, "\"1\"")]
    [InlineData(123456, "\"123456\"")]
    public async Task Write_ExpectedVersionSendsQuotedIfMatch(int version, string header)
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.WriteAsync("memory.md", "x", expectedVersion: version);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal(header, sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
    }

    [Fact]
    public async Task Write_CreateSendsIfNoneMatchStar()
    {
        var wire = Ok(WriteResult(version: 1, previousVersion: null, fileCreated: true), HttpStatusCode.Created, "\"1\"");
        await Mem(wire).Files.WriteAsync("memory.md", "x", create: true);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("*", sent.Header("If-None-Match"));
        Assert.Null(sent.Header("If-Match"));
    }

    [Fact]
    public async Task Write_NeitherOrBothPreconditionsRejectedWithoutHttp()
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        var neither = await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync("memory.md", "x"));
        Assert.Contains("exactly one of expectedVersion or create", neither.Message);
        await Assert.ThrowsAsync<ArgumentException>(
            () => files.WriteAsync("memory.md", "x", expectedVersion: 7, create: true));
        Assert.Empty(wire.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExpectedVersionBelowOneRejectedWithoutHttp(int version)
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => files.WriteAsync("memory.md", "x", expectedVersion: version));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => files.AppendAsync("memory.md", "x", expectedVersion: version));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => files.AppendSectionAsync("memory.md", "Notes", "x", expectedVersion: version));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => files.ReplaceSectionAsync("memory.md", "Notes", "x", expectedVersion: version));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => files.DeleteAsync("memory.md", expectedVersion: version));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task Append_WithoutExpectedVersionSendsNoPrecondition()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendAsync("memory.md", "- note");
        var sent = Assert.Single(wire.Requests);
        Assert.Null(sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
    }

    [Fact]
    public async Task Append_WithExpectedVersionSendsIfMatch()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendAsync("memory.md", "- note", expectedVersion: 3);
        Assert.Equal("\"3\"", Assert.Single(wire.Requests).Header("If-Match"));
    }

    [Fact]
    public async Task ReplaceSection_NeitherOrBothPreconditionsRejectedWithoutHttp()
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        await Assert.ThrowsAsync<ArgumentException>(
            () => files.ReplaceSectionAsync("memory.md", "Notes", "- n1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => files.ReplaceSectionAsync("memory.md", "Notes", "- n1", expectedVersion: 7, create: true));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task ReplaceSection_CreateSendsIfNoneMatchStar()
    {
        var wire = Ok(WriteResult(version: 1, previousVersion: null, fileCreated: true), HttpStatusCode.Created);
        await Mem(wire).Files.ReplaceSectionAsync("memory.md", "Notes", "- n1", create: true);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("*", sent.Header("If-None-Match"));
        Assert.Null(sent.Header("If-Match"));
    }

    [Fact]
    public async Task Delete_ExpectedVersionSendsIfMatch()
    {
        var wire = Ok(new { name = "memory.md", version = 4, hard = false, versions_deleted = 1 });
        await Mem(wire).Files.DeleteAsync("memory.md", expectedVersion: 4);
        var sent = Assert.Single(wire.Requests);
        Assert.Equal("\"4\"", sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
    }

    // ── Idempotency-Key ─────────────────────────────────────────────────

    public static readonly TheoryData<string> WriteOps = new() { "write", "append", "append_section", "replace_section" };

    private static Task<MemoryFileWriteResult> RunWrite(MemoryFiles files, string op, string? key = null) => op switch
    {
        "write" => files.WriteAsync("memory.md", "# Acme memory\n", expectedVersion: 7, idempotencyKey: key),
        "append" => files.AppendAsync("memory.md", "- note", idempotencyKey: key),
        "append_section" => files.AppendSectionAsync("memory.md", "Notes", "- note", idempotencyKey: key),
        "replace_section" => files.ReplaceSectionAsync("memory.md", "Notes", "- note", expectedVersion: 7, idempotencyKey: key),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    [Theory]
    [MemberData(nameof(WriteOps))]
    public async Task Writes_SendAUuidIdempotencyKey(string op)
    {
        var wire = Ok(WriteResult());
        await RunWrite(Mem(wire).Files, op);
        var key = Assert.Single(wire.Requests).Header("Idempotency-Key");
        Assert.NotNull(key);
        Assert.Matches(UuidV4, key!);
    }

    [Theory]
    [MemberData(nameof(WriteOps))]
    public async Task Writes_ReuseTheKeyAcrossABusyRetry(string op)
    {
        var wire = new WireRecorder((_, index) =>
        {
            if (index == 0)
            {
                var busy = WireRecorder.Json(
                    new { error = "file is busy", code = "memory_file_busy" }, HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return busy;
            }
            return WireRecorder.Json(WriteResult(), etag: "\"8\"");
        });
        var result = await RunWrite(Mem(wire).Files, op);

        Assert.Equal(8, result.Version);
        Assert.Equal(2, wire.Requests.Count);
        var first = wire.Requests[0];
        var second = wire.Requests[1];
        Assert.NotNull(first.Header("Idempotency-Key"));
        Assert.Equal(first.Header("Idempotency-Key"), second.Header("Idempotency-Key"));
        Assert.Equal(first.Body, second.Body);
        Assert.Equal(first.Header("If-Match"), second.Header("If-Match"));
        Assert.Equal(first.ContentType, second.ContentType);
    }

    [Fact]
    public async Task Writes_SeparateCallsGetDifferentKeys()
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        await files.AppendAsync("memory.md", "- one");
        await files.AppendAsync("memory.md", "- two");
        Assert.NotEqual(wire.Requests[0].Header("Idempotency-Key"), wire.Requests[1].Header("Idempotency-Key"));
    }

    [Theory]
    [MemberData(nameof(WriteOps))]
    public async Task Writes_SendACallerKeyVerbatim(string op)
    {
        var wire = Ok(WriteResult());
        await RunWrite(Mem(wire).Files, op, key: "client-key-42");
        Assert.Equal("client-key-42", Assert.Single(wire.Requests).Header("Idempotency-Key"));
    }

    [Fact]
    public async Task ReadAndDelete_SendNoIdempotencyKey()
    {
        var read = Ok(ReadBody(), etag: "\"7\"");
        await Mem(read).Files.ReadAsync("memory.md");
        Assert.Null(Assert.Single(read.Requests).Header("Idempotency-Key"));

        var delete = Ok(new { name = "memory.md", version = 7, hard = true, versions_deleted = 7 });
        await Mem(delete).Files.DeleteAsync("memory.md", hard: true);
        Assert.Null(Assert.Single(delete.Requests).Header("Idempotency-Key"));
    }

    // ── Version conflict ────────────────────────────────────────────────

    [Fact]
    public async Task Conflict412_IsTypedWithCurrentVersion()
    {
        var wire = Error((HttpStatusCode)412, new
        {
            error = "version conflict",
            code = "memory_file_version_conflict",
            current_version = 8,
            request_id = "req-1",
        });
        var ex = await Assert.ThrowsAsync<VersionConflictException>(
            () => Mem(wire).Files.WriteAsync("memory.md", "x", expectedVersion: 7));

        Assert.Equal(8, ex.CurrentVersion);
        AetherApiException api = ex;
        Assert.Equal((HttpStatusCode)412, api.StatusCode);
        Assert.Equal("memory_file_version_conflict", api.ErrorCode);
        Assert.Equal("version conflict", api.Body);
        Assert.False(api.IsRetryable);
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task Conflict412_NullCurrentVersion()
    {
        var wire = Error((HttpStatusCode)412, new Dictionary<string, object?>
        {
            ["error"] = "file does not exist",
            ["code"] = "memory_file_version_conflict",
            ["current_version"] = null,
        });
        var ex = await Assert.ThrowsAsync<VersionConflictException>(
            () => Mem(wire).Files.AppendAsync("memory.md", "x", expectedVersion: 3));
        Assert.Null(ex.CurrentVersion);
    }

    [Theory]
    [InlineData("{\"error\":\"conflict\",\"code\":\"memory_file_version_conflict\"}")]
    [InlineData("{\"error\":\"conflict\",\"code\":\"memory_file_version_conflict\",\"current_version\":\"8\"}")]
    [InlineData("{\"error\":\"conflict\",\"code\":\"memory_file_version_conflict\",\"current_version\":8.5}")]
    public async Task Conflict412_MissingOrNonIntegerCurrentVersionIsNull(string body)
    {
        var wire = new WireRecorder(() => new HttpResponseMessage((HttpStatusCode)412)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var ex = await Assert.ThrowsAsync<VersionConflictException>(
            () => Mem(wire).Files.DeleteAsync("memory.md", expectedVersion: 4));
        Assert.Null(ex.CurrentVersion);
        Assert.Equal("conflict", ex.Body);
    }

    [Fact]
    public void FromResponse_MapsTheConflictCode()
    {
        var ex = AetherApiException.FromResponse((HttpStatusCode)412, "conflict", "memory_file_version_conflict");
        Assert.Null(Assert.IsType<VersionConflictException>(ex).CurrentVersion);
        Assert.IsType<AetherApiException>(
            AetherApiException.FromResponse((HttpStatusCode)412, "conflict", "summary_revision_conflict"));
    }

    // Every request path builds its errors through the same helper, so the
    // conflict is typed (with current_version) on all of them.
    [Fact]
    public async Task Conflict412_IsTypedOnEveryTransportPath()
    {
        static WireRecorder Conflict() => new(() => WireRecorder.Json(
            new { error = "conflict", code = "memory_file_version_conflict", current_version = 5 },
            (HttpStatusCode)412));

        var calls = new Func<AetherClient, Task>[]
        {
            c => c.GetAsync("doc-1"),
            c => c.DeleteAsync("doc-1"),
            c => c.DownloadAsync("doc-1"),
            c => c.InsertStreamAsync(new MemoryStream(new byte[] { 1 })),
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<VersionConflictException>(() => call(Client(Conflict())));
            Assert.Equal(5, ex.CurrentVersion);
        }
    }

    // ── PATCH bodies ────────────────────────────────────────────────────

    [Fact]
    public async Task AppendSection_SendsPatchWithDefaultsExplicit()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendSectionAsync("memory.md", "Agreed terms", "x");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal("PATCH", sent.Method.Method);
        Assert.Equal("/v1/memory/files/memory.md", sent.Uri.AbsolutePath);
        Assert.StartsWith("application/json", sent.ContentType);
        WireRecorder.AssertJson(
            """{"op":"append_section","section":"Agreed terms","content":"x","level":2,"create_if_missing":true}""",
            sent.Body);
    }

    [Fact]
    public async Task Append_SendsNoSectionOrLevel()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendAsync("memory.md", "trailing");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal("PATCH", sent.Method.Method);
        WireRecorder.AssertJson("""{"op":"append","content":"trailing","create_if_missing":true}""", sent.Body);
    }

    [Fact]
    public async Task Append_CreateIfMissingFalseMapsThrough()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendAsync("memory.md", "trailing", createIfMissing: false);
        WireRecorder.AssertJson(
            """{"op":"append","content":"trailing","create_if_missing":false}""",
            Assert.Single(wire.Requests).Body);
    }

    [Fact]
    public async Task ReplaceSection_LevelAndCreateIfMissingMapThrough()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.ReplaceSectionAsync(
            "memory.md", "Key decisions", "- Use Stripe", expectedVersion: 7, level: 3, createIfMissing: false);
        WireRecorder.AssertJson(
            """{"op":"replace_section","section":"Key decisions","content":"- Use Stripe","level":3,"create_if_missing":false}""",
            Assert.Single(wire.Requests).Body);
    }

    [Fact]
    public async Task ReplaceSection_AllowsEmptyContent()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.ReplaceSectionAsync("memory.md", "Notes", "", expectedVersion: 7);
        Assert.Equal("", JsonDocument.Parse(Assert.Single(wire.Requests).Body!).RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AppendSection_IntoAnExistingSection()
    {
        var after = Section("Agreed terms", 2, "agreed-terms", 4, 7, 125);
        var wire = Ok(WriteResult(section: after));
        var result = await Mem(wire).Files.AppendSectionAsync(
            "memory.md", "Agreed terms", "- 2026-10-10 — Net 45 for EU entities (bob, PRJ-3)");

        WireRecorder.AssertJson(
            """{"op":"append_section","section":"Agreed terms","content":"- 2026-10-10 — Net 45 for EU entities (bob, PRJ-3)","level":2,"create_if_missing":true}""",
            Assert.Single(wire.Requests).Body);
        Assert.False(result.SectionCreated);
        Assert.Equal(7, result.Section!.EndLine);
        Assert.Equal(7, result.PreviousVersion);
    }

    [Fact]
    public async Task AppendSection_CreatesAMissingSection()
    {
        var created = Section("Key decisions", 2, "key-decisions", 11, 12, 42);
        var wire = Ok(WriteResult(section: created, sectionCreated: true), HttpStatusCode.Created);
        var result = await Mem(wire).Files.AppendSectionAsync("memory.md", "Key decisions", "- Use Stripe for billing");

        WireRecorder.AssertJson(
            """{"op":"append_section","section":"Key decisions","content":"- Use Stripe for billing","level":2,"create_if_missing":true}""",
            Assert.Single(wire.Requests).Body);
        Assert.True(result.SectionCreated);
        Assert.Equal("key-decisions", result.Section!.Slug);
    }

    [Fact]
    public async Task ReplaceSection_BySlugWithAVersion()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.ReplaceSectionAsync("memory.md", "agreed-terms", "- Net 60", expectedVersion: 7);

        var sent = Assert.Single(wire.Requests);
        Assert.Equal("PATCH", sent.Method.Method);
        Assert.Equal("\"7\"", sent.Header("If-Match"));
        Assert.Null(sent.Header("If-None-Match"));
        WireRecorder.AssertJson(
            """{"op":"replace_section","section":"agreed-terms","content":"- Net 60","level":2,"create_if_missing":true}""",
            sent.Body);
    }

    // ── read ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_ParsesTheMemoryFile()
    {
        var wire = Ok(ReadBody(), etag: "\"7\"");
        var file = await Mem(wire).Files.ReadAsync("memory.md", section: "Agreed terms");

        Assert.Equal(Owner, file.EntityId);
        Assert.Equal(PartitionHandle, file.Partition);
        Assert.Equal("memory.md", file.Name);
        Assert.Equal("3d1f0c2e-1111-4222-8333-944455556666", file.FileId);
        Assert.Equal(7, file.Version);
        Assert.True(file.IsCurrent);
        Assert.Equal(122, file.Bytes);
        Assert.Equal("scoped:ws_42:user:u_3", file.Author);
        Assert.Equal("key:ak_1a2b3c4d", file.Principal);
        Assert.Equal("append_section", file.Op);
        Assert.Equal("2026-09-01T10:00:00Z", file.CreatedAt);
        Assert.Equal(AgreedTermsContent, file.Content);
        Assert.Equal("agreed-terms", file.Section!.Slug);
        Assert.Equal(71, file.Section.Bytes);
        Assert.Equal(0, file.Offset);
        Assert.Null(file.NextOffset);
        Assert.Equal(71, file.TotalBytes);
        Assert.False(file.Truncated);

        Assert.Equal(4, file.Sections.Count);
        Assert.Equal(
            new[] { "acme-memory", "agreed-terms", "exceptions", "notes" },
            file.Sections.Select(s => s.Slug));
        var top = file.Sections[0];
        Assert.Equal(("Acme memory", 1, 1, 9, 122), (top.Heading, top.Level, top.StartLine, top.EndLine, top.Bytes));
        var notes = file.Sections[3];
        Assert.Equal((9, 9, 9), (notes.StartLine, notes.EndLine, notes.Bytes));
    }

    [Fact]
    public async Task Read_MapsOptionsToTheQuery()
    {
        var wire = Ok(ReadBody(), etag: "\"6\"");
        await Mem(wire).Files.ReadAsync("memory.md", version: 6, section: "Agreed terms", offset: 0, maxBytes: 1024);
        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("6", query["version"]);
        Assert.Equal("Agreed terms", query["section"]);
        Assert.Equal("0", query["offset"]);
        Assert.Equal("1024", query["max_bytes"]);
        Assert.False(query.ContainsKey("outline"));

        var outline = Ok(ReadBody(), etag: "\"7\"");
        await Mem(outline).Files.ReadAsync("memory.md", outline: true);
        var outlineQuery = WireRecorder.Query(Assert.Single(outline.Requests).Uri);
        Assert.Equal("true", outlineQuery["outline"]);
        Assert.False(outlineQuery.ContainsKey("version"));
        Assert.False(outlineQuery.ContainsKey("section"));
        Assert.False(outlineQuery.ContainsKey("offset"));
        Assert.False(outlineQuery.ContainsKey("max_bytes"));
    }

    [Fact]
    public async Task Read_TakesAMissingVersionFromTheETag()
    {
        var body = ReadBody();
        body.Remove("version");
        var wire = Ok(body, etag: "\"7\"");
        var file = await Mem(wire).Files.ReadAsync("memory.md");
        Assert.Equal(7, file.Version);
    }

    [Theory]
    [InlineData("\"7\"", 7)]
    [InlineData("\"123456\"", 123456)]
    [InlineData("7", 7)]
    [InlineData("W/\"7\"", 7)]
    [InlineData("\"abc\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseETagVersion(string? etag, int? expected) =>
        Assert.Equal(expected, MemoryFiles.ParseETagVersion(etag));

    // ── write ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_PutsRawMarkdownWithAuthor()
    {
        const string content = "# Acme memory\nIntro line.\n\n## Agreed terms\n- 2026-09-01 — Net 30\n";
        var wire = Ok(WriteResult(version: 1, previousVersion: null, fileCreated: true), HttpStatusCode.Created, "\"1\"");
        var result = await Mem(wire).Files.WriteAsync(
            "memory.md", content, create: true, author: "scoped:ws_42:user:u_3");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Put, sent.Method);
        Assert.Equal("/v1/memory/files/memory.md", sent.Uri.AbsolutePath);
        Assert.Equal("text/markdown; charset=utf-8", sent.ContentType);
        Assert.Equal(content, sent.Body);
        Assert.Equal("scoped:ws_42:user:u_3", WireRecorder.Query(sent.Uri)["author"]);

        Assert.True(result.FileCreated);
        Assert.Null(result.PreviousVersion);
        Assert.Equal(1, result.Version);
        Assert.False(result.Unchanged);
        Assert.Equal(4, result.Sections.Count);
    }

    [Fact]
    public async Task Write_OmitsAnEmptyAuthor()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.WriteAsync("memory.md", "x", expectedVersion: 7, author: "");
        Assert.False(WireRecorder.Query(Assert.Single(wire.Requests).Uri).ContainsKey("author"));
    }

    [Fact]
    public async Task Write_ParsesAnUnchangedResult()
    {
        var wire = Ok(WriteResult(version: 7, previousVersion: 7, unchanged: true), etag: "\"7\"");
        var result = await Mem(wire).Files.WriteAsync("memory.md", "same", expectedVersion: 7);
        Assert.True(result.Unchanged);
        Assert.Equal(7, result.Version);
        Assert.False(result.FileCreated);
    }

    [Fact]
    public async Task Write_TakesAMissingVersionFromTheETag()
    {
        var body = WriteResult();
        body.Remove("version");
        var wire = Ok(body, etag: "\"9\"");
        var result = await Mem(wire).Files.AppendAsync("memory.md", "x");
        Assert.Equal(9, result.Version);
    }

    // ── list / versions / diff / grep / delete ──────────────────────────

    [Fact]
    public async Task List_MapsPrefixLimitCursor()
    {
        var wire = Ok(new { files = new[] { Info(), Info(3) }, next_cursor = "next-page" });
        var page = await Mem(wire).Files.ListAsync(prefix: "notes", limit: 2, cursor: "abc");

        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("notes", query["prefix"]);
        Assert.Equal("2", query["limit"]);
        Assert.Equal("abc", query["cursor"]);
        Assert.Equal(2, page.Files.Count);
        Assert.Equal(3, page.Files[1].Version);
        Assert.Equal("next-page", page.NextCursor);
    }

    [Fact]
    public async Task Versions_MapsAndParses()
    {
        var wire = Ok(new
        {
            entity_id = Owner,
            partition = (string?)null,
            name = "memory.md",
            file_id = "f-1",
            current_version = 7,
            versions = new object[]
            {
                new { doc_id = "d7", version = 7, bytes = 122, content_hash = "hash-07", author = (string?)null, principal = "key:ak_1", op = "append_section", updated_at = "2026-10-10T09:12:44Z", is_current = true },
                new { doc_id = "d6", version = 6, bytes = 100, content_hash = "hash-06", author = "bob", principal = "key:ak_1", op = "put", updated_at = "2026-10-09T09:00:00Z", is_current = false },
            },
        });
        var versions = await Mem(wire).Files.VersionsAsync("memory.md", limit: 10);

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/v1/memory/files/memory.md/versions", sent.Uri.AbsolutePath);
        var query = WireRecorder.Query(sent.Uri);
        Assert.Equal("10", query["limit"]);
        Assert.Equal(Owner, query["entity_id"]);

        Assert.Null(versions.Partition);
        Assert.Equal(7, versions.CurrentVersion);
        Assert.Equal(new[] { 7, 6 }, versions.Versions.Select(v => v.Version));
        Assert.True(versions.Versions[0].IsCurrent);
        Assert.Null(versions.Versions[0].Author);
        Assert.Equal("bob", versions.Versions[1].Author);
        Assert.Equal("put", versions.Versions[1].Op);
    }

    private static object DiffBody() => new
    {
        entity_id = Owner,
        partition = PartitionHandle,
        name = "memory.md",
        file_id = "f-1",
        from_version = 6,
        to_version = 7,
        context = 3,
        diff = "--- memory.md@v6\n+++ memory.md@v7\n@@ -4,3 +4,4 @@\n",
        added_lines = 1,
        removed_lines = 0,
        approximate = false,
        truncated = false,
    };

    [Fact]
    public async Task Diff_SendsDefaultContextAndParses()
    {
        var wire = Ok(DiffBody());
        var diff = await Mem(wire).Files.DiffAsync("memory.md");

        var sent = Assert.Single(wire.Requests);
        Assert.Equal("/v1/memory/files/memory.md/diff", sent.Uri.AbsolutePath);
        var query = WireRecorder.Query(sent.Uri);
        Assert.Equal("3", query["context"]);
        Assert.False(query.ContainsKey("from"));
        Assert.False(query.ContainsKey("to"));

        Assert.Equal(6, diff.FromVersion);
        Assert.Equal(7, diff.ToVersion);
        Assert.Equal(3, diff.Context);
        Assert.StartsWith("--- memory.md@v6\n+++ memory.md@v7\n", diff.Diff);
        Assert.Equal(1, diff.AddedLines);
        Assert.Equal(0, diff.RemovedLines);
        Assert.False(diff.Approximate);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task Diff_MapsFromToAndContext()
    {
        var wire = Ok(DiffBody());
        await Mem(wire).Files.DiffAsync("memory.md", fromVersion: 0, toVersion: 7, context: 1);
        var query = WireRecorder.Query(Assert.Single(wire.Requests).Uri);
        Assert.Equal("0", query["from"]);
        Assert.Equal("7", query["to"]);
        Assert.Equal("1", query["context"]);
    }

    [Fact]
    public async Task Diff_RejectsOutOfRangeVersionsWithoutHttp()
    {
        var wire = Ok(DiffBody());
        var files = Mem(wire).Files;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.DiffAsync("memory.md", fromVersion: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.DiffAsync("memory.md", toVersion: 0));
        Assert.Empty(wire.Requests);
    }

    private static object GrepBody() => new
    {
        matches = new[]
        {
            new
            {
                entity_id = Owner,
                partition = PartitionHandle,
                name = "memory.md",
                version = 7,
                line = 5,
                section = "Agreed terms",
                section_slug = "agreed-terms",
                text = "- 2026-09-01 — Net 30 payment terms (alice, ISS-12)",
                before = new[] { "## Agreed terms" },
                after = new[] { "" },
            },
        },
        truncated = false,
        truncated_reason = (string?)null,
        files_scanned = 1,
        bytes_scanned = 122,
    };

    [Fact]
    public async Task Grep_SendsEveryDefaultAndNoFiles()
    {
        var wire = Ok(GrepBody());
        var result = await Mem(wire).Files.GrepAsync("Net 30");

        WireRecorder.AssertJson(
            $$"""{"entity_id":"{{Owner}}","pattern":"Net 30","regex":false,"ignore_case":false,"context":0,"max_matches":100}""",
            Assert.Single(wire.Requests).Body);

        var match = Assert.Single(result.Matches);
        Assert.Equal(PartitionHandle, match.Partition);
        Assert.Equal(7, match.Version);
        Assert.Equal(5, match.Line);
        Assert.Equal("Agreed terms", match.Section);
        Assert.Equal("agreed-terms", match.SectionSlug);
        Assert.Equal("- 2026-09-01 — Net 30 payment terms (alice, ISS-12)", match.Text);
        Assert.Equal(new[] { "## Agreed terms" }, match.Before);
        Assert.Equal(new[] { "" }, match.After);
        Assert.False(result.Truncated);
        Assert.Null(result.TruncatedReason);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(122, result.BytesScanned);
    }

    [Fact]
    public async Task Grep_MapsEveryOption()
    {
        var wire = Ok(new { matches = Array.Empty<object>(), truncated = true, truncated_reason = "max_matches", files_scanned = 2, bytes_scanned = 10 });
        var result = await Mem(wire).Files.GrepAsync(
            "net\\s+\\d+", regex: true, ignoreCase: true, files: new[] { "memory.md" }, context: 1, maxMatches: 5);

        WireRecorder.AssertJson(
            $$"""{"entity_id":"{{Owner}}","pattern":"net\\s+\\d+","regex":true,"ignore_case":true,"files":["memory.md"],"context":1,"max_matches":5}""",
            Assert.Single(wire.Requests).Body);
        Assert.True(result.Truncated);
        Assert.Equal("max_matches", result.TruncatedReason);
    }

    [Fact]
    public async Task Grep_RejectsAnEmptyPatternWithoutHttp()
    {
        var wire = Ok(GrepBody());
        await Assert.ThrowsAsync<ArgumentException>(() => Mem(wire).Files.GrepAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(wire).GrepMemoryFilesAsync(""));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task Delete_HardMapsAndParses()
    {
        var wire = Ok(new { entity_id = Owner, partition = PartitionHandle, name = "memory.md", file_id = "f-1", version = 7, hard = true, versions_deleted = 7 });
        var result = await Mem(wire).Files.DeleteAsync("memory.md", hard: true);

        var sent = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Delete, sent.Method);
        Assert.Equal("/v1/memory/files/memory.md", sent.Uri.AbsolutePath);
        Assert.Equal("true", WireRecorder.Query(sent.Uri)["hard"]);
        Assert.Null(sent.Header("If-Match"));
        Assert.True(result.Hard);
        Assert.Equal(7, result.VersionsDeleted);
        Assert.Equal("f-1", result.FileId);
    }

    [Fact]
    public async Task Delete_SoftSendsNoHardParam()
    {
        var wire = Ok(new { name = "memory.md", version = 7, hard = false, versions_deleted = 1 });
        var result = await Mem(wire).Files.DeleteAsync("memory.md");
        Assert.False(WireRecorder.Query(Assert.Single(wire.Requests).Uri).ContainsKey("hard"));
        Assert.Equal(1, result.VersionsDeleted);
    }

    // ── Argument validation ─────────────────────────────────────────────

    [Fact]
    public async Task Validation_RejectsBadArgumentsWithoutHttp()
    {
        var wire = Ok(WriteResult());
        var files = Mem(wire).Files;
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendAsync("memory.md", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendSectionAsync("memory.md", "Notes", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendSectionAsync("memory.md", "  ", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => files.AppendSectionAsync("memory.md", "", "x"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => files.ReplaceSectionAsync("memory.md", " \t", "x", expectedVersion: 7));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => files.WriteAsync("memory.md", null!, expectedVersion: 7));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task Append_AllowsWhitespaceContent()
    {
        var wire = Ok(WriteResult());
        await Mem(wire).Files.AppendAsync("memory.md", "\n");
        Assert.Single(wire.Requests);
    }

    // ── Error passthrough ───────────────────────────────────────────────

    [Fact]
    public async Task CreditExhausted_FromAWriteIsTyped()
    {
        var wire = Error((HttpStatusCode)402, new { error = "Top up your balance", code = "credit_exhausted" });
        await Assert.ThrowsAsync<CreditExhaustedException>(
            () => Mem(wire).Files.WriteAsync("memory.md", "x", expectedVersion: 7));
    }

    [Theory]
    [InlineData(428, "precondition_required")]
    [InlineData(409, "memory_file_section_ambiguous")]
    [InlineData(404, "feature_not_enabled")]
    [InlineData(404, "memory_file_not_found")]
    [InlineData(413, "memory_file_too_large")]
    [InlineData(409, "idempotency_key_reused")]
    public async Task OtherCodes_SurfaceAsTheBaseApiError(int status, string code)
    {
        var wire = Error((HttpStatusCode)status, new
        {
            error = "rejected",
            code,
            candidates = new[] { new { heading = "Notes", slug = "notes", start_line = 16 } },
        });
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => Mem(wire).Files.AppendSectionAsync("memory.md", "notes", "x"));
        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal("rejected", ex.Body);
    }

    // ── Search hits ─────────────────────────────────────────────────────

    private static Dictionary<string, object?> Hit(object? memoryFile)
    {
        var hit = new Dictionary<string, object?>
        {
            ["doc_id"] = "doc-1",
            ["score"] = 88,
            ["content_type"] = "text/markdown",
            ["content"] = "inline content",
            ["tags"] = Array.Empty<string>(),
            ["metadata"] = new Dictionary<string, object>(),
        };
        if (memoryFile != null)
            hit["memory_file"] = memoryFile;
        return hit;
    }

    [Fact]
    public async Task SearchHits_ParseMemoryFile()
    {
        var memoryFile = new { name = "memory.md", version = 7, section = "Agreed terms", section_slug = "agreed-terms" };
        var wire = Ok(new { query = "q", results = new[] { Hit(memoryFile), Hit(null) } });
        var hits = await Client(wire).SearchAsync("net 30");

        var file = hits[0].MemoryFile!;
        Assert.Equal("memory.md", file.Name);
        Assert.Equal(7, file.Version);
        Assert.Equal("Agreed terms", file.Section);
        Assert.Equal("agreed-terms", file.SectionSlug);
        Assert.Null(hits[1].MemoryFile);
    }

    [Fact]
    public async Task RetrieveHits_CarryMemoryFile()
    {
        var memoryFile = new Dictionary<string, object?>
        {
            ["name"] = "memory.md",
            ["version"] = 7,
            ["section"] = null,
            ["section_slug"] = null,
        };
        var wire = Ok(new { query = "q", results = new[] { Hit(memoryFile) } });
        var result = Assert.Single(await Client(wire).RetrieveAsync("net 30"));
        Assert.Equal("memory.md", result.MemoryFile!.Name);
        Assert.Null(result.MemoryFile.Section);
        Assert.Equal("inline content", result.Content);
    }
}
