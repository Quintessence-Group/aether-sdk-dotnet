using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Tests for the directory/batch ingestion helpers:
/// <see cref="AetherClient.IngestFilesAsync"/> and
/// <see cref="AetherClient.IngestDirectoryAsync"/>.
/// </summary>
public class IngestTests : IDisposable
{
    private readonly string _tempDir;

    public IngestTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aether-ingest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; never fail a test on teardown.
        }
        GC.SuppressFinalize(this);
    }

    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    // Builds a stock document-record response body for a successful insert.
    private static HttpResponseMessage OkInsert(string docId) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    doc_id = docId,
                    cid = "hash",
                    content_type = "text/plain",
                    size_bytes = 1,
                    chunks = 1,
                    vectors = 1,
                    version = 1,
                }),
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ErrorBody(HttpStatusCode status, string error) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json"),
        };

    private string WriteFile(string name, string content = "hello world")
    {
        var path = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── IngestFilesAsync ──────────────────────────────────────────────

    // A mixed batch where the engine returns 422 for one file: the good file is
    // "ingested" and the rejected one is "skipped" — never thrown, never dropped.
    [Fact]
    public async Task IngestFiles_MixedBatch_ReportsIngestedAndSkipped()
    {
        var good = WriteFile("good.md", "# Title\n\nbody");
        var bad = WriteFile("bad.bin", "\x00\x01binary");

        var handler = new MockHttpMessageHandler(req =>
        {
            var query = req.RequestUri!.Query;
            // 422 (unprocessable) for the binary file; OK for everything else.
            if (query.Contains("filename=bad.bin"))
                return ErrorBody((HttpStatusCode)422, "Unprocessable: unknown or binary type");
            return OkInsert("doc-good");
        });

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { good, bad });

        Assert.Equal(2, results.Count);

        Assert.Equal(good, results[0].Path);
        Assert.Equal("ingested", results[0].Status);
        Assert.Equal("doc-good", results[0].DocId);
        Assert.Equal("text/markdown", results[0].ContentType);
        Assert.Null(results[0].Error);

        Assert.Equal(bad, results[1].Path);
        Assert.Equal("skipped", results[1].Status);
        Assert.Null(results[1].DocId);
        Assert.Equal("Unprocessable: unknown or binary type", results[1].Error);
    }

    // 413 (too large) and 415 (unsupported media) are also classified as "skipped".
    [Theory]
    [InlineData(413)]
    [InlineData(415)]
    [InlineData(422)]
    public async Task IngestFiles_RejectionStatuses_AreSkipped(int statusCode)
    {
        var file = WriteFile("doc.pdf");
        var handler = new MockHttpMessageHandler(_ =>
            ErrorBody((HttpStatusCode)statusCode, $"rejected {statusCode}"));

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { file });

        Assert.Single(results);
        Assert.Equal("skipped", results[0].Status);
        Assert.Equal("application/pdf", results[0].ContentType);
    }

    // A non-rejection API error (e.g. 500) is reported as "error", not "skipped".
    [Fact]
    public async Task IngestFiles_OtherApiError_IsError()
    {
        var file = WriteFile("doc.txt");
        var handler = new MockHttpMessageHandler(_ =>
            ErrorBody(HttpStatusCode.InternalServerError, "boom"));

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { file });

        Assert.Single(results);
        Assert.Equal("error", results[0].Status);
        Assert.Equal("boom", results[0].Error);
    }

    // A file-read failure (missing path → FileNotFoundException : IOException) is
    // reported as "error" and never reaches the engine.
    [Fact]
    public async Task IngestFiles_UnreadableFile_IsError()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.txt");
        var handler = new MockHttpMessageHandler(_ => OkInsert("never"));

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { missing });

        Assert.Single(results);
        Assert.Equal("error", results[0].Status);
        Assert.Null(results[0].DocId);
        Assert.NotNull(results[0].Error);
        Assert.Null(handler.LastRequest); // the engine was never called
    }

    // raiseOnError re-throws the engine rejection instead of reporting it.
    [Fact]
    public async Task IngestFiles_RaiseOnError_RethrowsApiException()
    {
        var file = WriteFile("doc.bin");
        var handler = new MockHttpMessageHandler(_ =>
            ErrorBody((HttpStatusCode)422, "nope"));

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.IngestFilesAsync(new[] { file }, raiseOnError: true));
        Assert.Equal((HttpStatusCode)422, ex.StatusCode);
    }

    // raiseOnError also re-throws a file-read failure.
    [Fact]
    public async Task IngestFiles_RaiseOnError_RethrowsReadFailure()
    {
        var missing = Path.Combine(_tempDir, "missing.txt");
        var handler = new MockHttpMessageHandler(_ => OkInsert("never"));

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => client.IngestFilesAsync(new[] { missing }, raiseOnError: true));
    }

    // .md resolves to text/markdown (and is forwarded as the insert content_type).
    [Fact]
    public async Task IngestFiles_MarkdownResolvesToTextMarkdown()
    {
        var file = WriteFile("notes.md", "# notes");
        var handler = new MockHttpMessageHandler(_ => OkInsert("doc-md"));

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { file });

        Assert.Equal("text/markdown", results[0].ContentType);
        Assert.Contains("content_type=text%2Fmarkdown", handler.LastRequest!.RequestUri!.Query);
    }

    // An unlisted extension falls back to application/octet-stream.
    [Fact]
    public async Task IngestFiles_UnknownExtension_FallsBackToOctetStream()
    {
        var file = WriteFile("data.xyz");
        var handler = new MockHttpMessageHandler(_ => OkInsert("doc-xyz"));

        using var client = CreateClient(handler);
        var results = await client.IngestFilesAsync(new[] { file });

        Assert.Equal("application/octet-stream", results[0].ContentType);
    }

    // Optional params (tags, chunking, entityId, source) are forwarded to the insert.
    [Fact]
    public async Task IngestFiles_ForwardsOptionalParams()
    {
        var file = WriteFile("doc.txt");
        var handler = new MockHttpMessageHandler(_ => OkInsert("doc-1"));

        using var client = CreateClient(handler);
        await client.IngestFilesAsync(
            new[] { file },
            tags: new[] { "a", "b" },
            chunking: new ChunkingConfig { ChunkSize = 256, Overlap = 32 },
            entityId: "acct/42",
            source: "vault");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("tags=a%2Cb", query);
        Assert.Contains("chunk_size=256", query);
        Assert.Contains("overlap=32", query);
        Assert.Contains("entity_id=acct%2F42", query);
        Assert.Contains("source=vault", query);
    }

    // ── IngestDirectoryAsync ──────────────────────────────────────────

    // Recursive (default) walk picks up nested files; extension filter limits which
    // files are loaded. Leading dots and case on the filter are optional.
    [Fact]
    public async Task IngestDirectory_RecursiveWithExtensionFilter()
    {
        WriteFile("top.md", "# top");
        WriteFile("skip.log", "noise");
        WriteFile(Path.Combine("nested", "deep.md"), "# deep");
        WriteFile(Path.Combine("nested", "also.txt"), "text");

        var inserted = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            inserted.Add(req.RequestUri!.Query);
            return OkInsert("doc");
        });

        using var client = CreateClient(handler);
        // Mixed forms: ".md" with dot, "txt" without — both should match.
        var results = await client.IngestDirectoryAsync(
            _tempDir, extensions: new[] { ".md", "TXT" });

        // top.md + nested/deep.md + nested/also.txt = 3; skip.log excluded.
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("ingested", r.Status));
        Assert.Equal(3, inserted.Count);
        Assert.DoesNotContain(inserted, q => q.Contains("skip.log"));
    }

    // recursive: false ingests only the top-level files.
    [Fact]
    public async Task IngestDirectory_NonRecursive_SkipsSubdirectories()
    {
        WriteFile("top.txt", "top");
        WriteFile(Path.Combine("nested", "deep.txt"), "deep");

        var handler = new MockHttpMessageHandler(_ => OkInsert("doc"));
        using var client = CreateClient(handler);
        var results = await client.IngestDirectoryAsync(_tempDir, recursive: false);

        Assert.Single(results);
        Assert.EndsWith("top.txt", results[0].Path);
    }

    // No extension filter ingests every file.
    [Fact]
    public async Task IngestDirectory_NoFilter_IngestsAllFiles()
    {
        WriteFile("a.md");
        WriteFile("b.txt");
        WriteFile("c.pdf");

        var handler = new MockHttpMessageHandler(_ => OkInsert("doc"));
        using var client = CreateClient(handler);
        var results = await client.IngestDirectoryAsync(_tempDir);

        Assert.Equal(3, results.Count);
    }

    // A path that is not a directory throws DirectoryNotFoundException.
    [Fact]
    public async Task IngestDirectory_NotADirectory_Throws()
    {
        var file = WriteFile("solo.txt");
        var handler = new MockHttpMessageHandler(_ => OkInsert("doc"));
        using var client = CreateClient(handler);

        // A regular file is not a directory.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => client.IngestDirectoryAsync(file));

        // A path that does not exist at all is not a directory either.
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => client.IngestDirectoryAsync(Path.Combine(_tempDir, "no-such-dir")));
    }
}
