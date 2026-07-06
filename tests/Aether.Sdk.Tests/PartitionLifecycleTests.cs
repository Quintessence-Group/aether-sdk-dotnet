using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Partition lifecycle (list / delete), provable isolation (trace /
/// verify-isolation), the partition guard on doc_id-addressed routes, the
/// explicit move, the partition echo on responses, and the partition_required
/// typed exception. Each test drives a real client over the mocked transport
/// so the genuine request / parse / error-mapping path runs (mirrors
/// sdk/python/tests/test_partitions.py).
/// </summary>
public class PartitionLifecycleTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    // ── ListPartitions ───────────────────────────────────────────────

    [Fact]
    public async Task ListPartitions_GetsPartitionsRoute()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            partitions = Array.Empty<object>(),
            count = 0,
            warnings = Array.Empty<object>(),
        });

        using var client = CreateClient(handler);
        await client.ListPartitionsAsync();

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v1/partitions", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListPartitions_ParsesCountsAndWarnings()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            partitions = new[]
            {
                new { id = "client-a", document_count = 3 },
                new { id = "client-b", document_count = 1 },
            },
            count = 2,
            warnings = new[]
            {
                new
                {
                    kind = "single_document",
                    partitions = new[] { "client-b" },
                    detail = "holds a single document",
                },
            },
        });

        using var client = CreateClient(handler);
        var listing = await client.ListPartitionsAsync();

        Assert.Equal(new[] { "client-a", "client-b" }, listing.Partitions.Select(p => p.Id));
        Assert.Equal(3, listing.Partitions[0].DocumentCount);
        Assert.Equal(1, listing.Partitions[1].DocumentCount);

        var w = Assert.Single(listing.Warnings);
        Assert.Equal("single_document", w.Kind);
        Assert.Equal(new[] { "client-b" }, w.Partitions);
        Assert.Equal("holds a single document", w.Detail);
    }

    [Fact]
    public async Task ListPartitions_NotScopedByHandle()
    {
        // Tenant-level: even derived from a scoped handle it sends no partition param.
        var handler = MockHttpMessageHandler.WithJson(new
        {
            partitions = Array.Empty<object>(),
            warnings = Array.Empty<object>(),
        });

        using var client = CreateClient(handler);
        await client.Partition("tenant-x").ListPartitionsAsync();

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    // ── DeletePartition ──────────────────────────────────────────────

    [Fact]
    public async Task DeletePartition_ReturnsCountAndEncodesPath()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            status = "deleted",
            partition = "client/42",
            documents_deleted = 7,
        });

        using var client = CreateClient(handler);
        var deleted = await client.DeletePartitionAsync("client/42");

        Assert.Equal(7, deleted);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        // The id is URL-encoded into the path segment (slash → %2F).
        Assert.Equal("/v1/partitions/client%2F42", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DeletePartition_IdempotentZeroOnUnknown()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            status = "deleted",
            partition = "ghost",
            documents_deleted = 0,
        });

        using var client = CreateClient(handler);
        Assert.Equal(0, await client.DeletePartitionAsync("ghost"));
    }

    [Fact]
    public async Task DeletePartition_RejectsEmptyId_NoHttpCall()
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

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeletePartitionAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeletePartitionAsync("   "));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DeletePartition_RejectsTooLongId_NoHttpCall()
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

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.DeletePartitionAsync(new string('a', 257)));
        Assert.Equal(0, calls);
    }

    // ── SearchTrace ──────────────────────────────────────────────────

    private static MockHttpMessageHandler TraceHandler(
        string[] partitionsTouched, bool defaultTouched = false, int results = 1)
    {
        var hits = results > 0
            ? new object[] { new { doc_id = "d1", score = 90, content_type = "text/plain" } }
            : Array.Empty<object>();

        return new MockHttpMessageHandler(req =>
        {
            // The handle injects the partition and the trace flag.
            Assert.Contains("trace=true", req.RequestUri!.Query);
            Assert.Contains("partition=client-a", req.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        query = "q",
                        results = hits,
                        trace = new
                        {
                            scoped_to = "client-a",
                            partitions_touched = partitionsTouched,
                            default_partition_touched = defaultTouched,
                            results,
                            candidates_in_scope = 1,
                            boundary = "partition",
                        },
                    }),
                    Encoding.UTF8, "application/json"),
            };
        });
    }

    [Fact]
    public async Task SearchTrace_ReturnsResultsAndTrace()
    {
        using var client = CreateClient(TraceHandler(new[] { "client-a" }));
        var traced = await client.Partition("client-a").SearchTraceAsync("returns policy");

        Assert.Equal("client-a", traced.Trace.ScopedTo);
        Assert.Equal(new[] { "client-a" }, traced.Trace.PartitionsTouched);
        Assert.Equal(1, traced.Trace.CandidatesInScope);
        Assert.Equal("partition", traced.Trace.Boundary);
        Assert.False(traced.Trace.DefaultPartitionTouched);

        // Results parse exactly like Search (score carried through SearchResult).
        var hit = Assert.Single(traced.Results);
        Assert.Equal("d1", hit.DocId);
        Assert.Equal(90, hit.Score);
    }

    [Fact]
    public async Task SearchTrace_UnscopedSendsNoPartitionButTraceFlag()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "q",
            results = Array.Empty<object>(),
            trace = new
            {
                scoped_to = (string?)null,
                partitions_touched = Array.Empty<string>(),
                default_partition_touched = false,
                results = 0,
                candidates_in_scope = (int?)null,
                boundary = "tenant",
            },
        });

        using var client = CreateClient(handler);
        var traced = await client.SearchTraceAsync("q");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("trace=true", query);
        Assert.DoesNotContain("partition", query);
        Assert.Null(traced.Trace.ScopedTo);
        Assert.Null(traced.Trace.CandidatesInScope);
        Assert.Equal("tenant", traced.Trace.Boundary);
    }

    // ── VerifyIsolation ──────────────────────────────────────────────

    [Fact]
    public async Task VerifyIsolation_OkWhenScopeHolds()
    {
        using var client = CreateClient(TraceHandler(new[] { "client-a" }));
        var check = await client.Partition("client-a").VerifyIsolationAsync("returns policy");

        Assert.True(check.Ok);
        Assert.Empty(check.Leaked);
        Assert.Equal("client-a", check.ScopedTo);
        Assert.Equal(1, check.Results);
        Assert.Equal(1, check.CandidatesInScope);
    }

    [Fact]
    public async Task VerifyIsolation_FlagsALeak()
    {
        using var client = CreateClient(TraceHandler(new[] { "client-a", "client-b" }));
        var check = await client.Partition("client-a").VerifyIsolationAsync("returns policy");

        Assert.False(check.Ok);
        Assert.Equal(new[] { "client-b" }, check.Leaked);
    }

    [Fact]
    public async Task VerifyIsolation_FlagsDefaultPartitionTouch()
    {
        using var client = CreateClient(TraceHandler(new[] { "client-a" }, defaultTouched: true));
        var check = await client.Partition("client-a").VerifyIsolationAsync("returns policy");

        // No foreign partition leaked, but a default-partition touch still fails the check.
        Assert.False(check.Ok);
        Assert.Empty(check.Leaked);
    }

    [Fact]
    public async Task VerifyIsolation_RequiresAHandle()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.VerifyIsolationAsync("returns policy"));
    }

    // ── Partition guard on doc_id-addressed routes ───────────────────

    private static MockHttpMessageHandler DocRecordHandler(string? partition = null) =>
        MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 1,
            partition,
        });

    [Fact]
    public async Task PartitionGuard_Get_InjectsPartition()
    {
        var handler = DocRecordHandler("client-a");
        using var client = CreateClient(handler);

        await client.Partition("client-a").GetAsync("abc-123");

        Assert.Equal("/v1/documents/abc-123", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("partition=client-a", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task PartitionGuard_Download_InjectsPartition()
    {
        var handler = MockHttpMessageHandler.WithBytes(Encoding.UTF8.GetBytes("hello"));
        using var client = CreateClient(handler);

        await client.Partition("client-a").DownloadAsync("abc-123");

        Assert.Equal("/v1/documents/abc-123/download", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("partition=client-a", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task PartitionGuard_SoftDelete_InjectsPartition()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", doc_id = "abc-123" });
        using var client = CreateClient(handler);

        await client.Partition("client-a").DeleteAsync("abc-123");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Contains("partition=client-a", handler.LastRequest.RequestUri!.Query);
        Assert.DoesNotContain("hard", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task PartitionGuard_HardDelete_InjectsPartitionAlongsideHardFlag()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "deleted", doc_id = "abc-123" });
        using var client = CreateClient(handler);

        await client.Partition("client-a").HardDeleteAsync("abc-123");

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("hard=true", query);
        Assert.Contains("partition=client-a", query);
    }

    [Fact]
    public async Task PartitionGuard_Restore_InjectsPartition()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "restored", doc_id = "abc-123" });
        using var client = CreateClient(handler);

        await client.Partition("client-a").RestoreAsync("abc-123");

        Assert.Equal("/v1/documents/abc-123/restore", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("partition=client-a", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task PartitionGuard_BackfillEntity_InjectsPartition()
    {
        // The backfill scan is partition-constrained under a handle (a
        // multi-tenant key requires it).
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

        await client.Partition("client-a").BackfillEntityFromTagsAsync("patient:");

        Assert.Contains("partition=client-a", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task PartitionGuard_UnscopedClient_SendsNone()
    {
        // Bare doc-id calls on the unscoped client are byte-identical to the
        // pre-guard behavior: no partition param on any by-ID route.
        var handler = DocRecordHandler();
        using var client = CreateClient(handler);

        await client.GetAsync("abc-123");
        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);

        await client.DeleteAsync("abc-123");
        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);

        await client.RestoreAsync("abc-123");
        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task PartitionGuard_UrlEncodesValue()
    {
        var handler = DocRecordHandler("acme/eu");
        using var client = CreateClient(handler);

        await client.Partition("acme/eu").GetAsync("abc-123");

        // Same encoding as entity_id ('/' → %2F).
        Assert.Contains("partition=acme%2Feu", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task PartitionGuard_Mismatch_SurfacesGenuineMiss404()
    {
        // A wrong guard is byte-identical to a nonexistent id — never a
        // partition-existence oracle.
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "document not found: abc-123", code = "document_not_found" },
            HttpStatusCode.NotFound);
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.Partition("client-b").GetAsync("abc-123"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("document_not_found", ex.ErrorCode);
    }

    // ── MoveDocument ─────────────────────────────────────────────────

    [Fact]
    public async Task Move_PostsBothFieldsAndReturnsUpdatedRecord()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "abc-123",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 2,
            partition = "client-b",
        });
        using var client = CreateClient(handler);

        var record = await client.MoveDocumentAsync("abc-123", "client-a", "client-b");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/documents/abc-123/move", handler.LastRequest.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
        Assert.Equal("client-a", body.RootElement.GetProperty("expect_partition").GetString());

        // The response echoes the new home; version incremented on a real move.
        Assert.Equal("client-b", record.Partition);
        Assert.Equal(2, record.Version);
    }

    [Fact]
    public async Task Move_NullNamesTheDefaultPartition_ExplicitNullOnWire()
    {
        // Both keys must always be PRESENT: an explicit JSON null names the
        // default partition (an omitted key is a server-side 400).
        var handler = DocRecordHandler("client-b");
        using var client = CreateClient(handler);

        await client.MoveDocumentAsync("abc-123", null, "client-b");

        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expect_partition").ValueKind);
            Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
        }

        await client.MoveDocumentAsync("abc-123", "client-a", null);

        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal("client-a", body.RootElement.GetProperty("expect_partition").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("to_partition").ValueKind);
        }
    }

    [Fact]
    public async Task Move_NotAutoScopedByHandle()
    {
        // A relocating call names its partitions explicitly (like
        // DeletePartition): the handle injects nothing into the move.
        var handler = DocRecordHandler("client-b");
        using var client = CreateClient(handler);

        await client.Partition("tenant-x").MoveDocumentAsync("abc-123", null, "client-b");

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        // The handle's scope must not overwrite the explicit (null) assertion.
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expect_partition").ValueKind);
        Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
    }

    [Fact]
    public async Task Move_WrongAssertion_SurfacesGenuineMiss404()
    {
        // Wrong expect / missing / tombstoned all collapse into the identical
        // document_not_found 404.
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "document not found: abc-123", code = "document_not_found" },
            HttpStatusCode.NotFound);
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<AetherApiException>(
            () => client.MoveDocumentAsync("abc-123", "client-b", "client-c"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("document_not_found", ex.ErrorCode);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Move_RejectsEmptyDocId_NoHttpCall()
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

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MoveDocumentAsync("", "client-a", "client-b"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Move_RejectsInvalidNamedPartitions_ButNotNull_NoHttpCall()
    {
        // Non-null names follow the handle's validation rule; null is exempt —
        // it is the meaningful "default partition" value, never rejected client-side.
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

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MoveDocumentAsync("abc-123", "   ", "client-b"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.MoveDocumentAsync("abc-123", "client-a", new string('a', 257)));
        Assert.Equal(0, calls);
    }

    // ── Partition echo on responses ──────────────────────────────────

    [Fact]
    public async Task PartitionEcho_ParsedFromDocumentRecord()
    {
        using (var client = CreateClient(DocRecordHandler("client-a")))
        {
            var record = await client.GetAsync("abc-123");
            Assert.Equal("client-a", record.Partition);
        }

        // Explicit null = the default partition.
        using (var client = CreateClient(DocRecordHandler()))
        {
            var record = await client.GetAsync("abc-123");
            Assert.Null(record.Partition);
        }
    }

    [Fact]
    public async Task PartitionEcho_ParsedFromListItems()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = new object[]
            {
                new { doc_id = "d1", cid = "c1", content_type = "text/plain", size_bytes = 1, chunks = 1, vectors = 1, version = 1, partition = "client-a" },
                new { doc_id = "d2", cid = "c2", content_type = "text/plain", size_bytes = 1, chunks = 1, vectors = 1, version = 1, partition = (string?)null },
            },
            count = 2,
            total = 2,
            has_more = false,
        });
        using var client = CreateClient(handler);

        var listing = await client.ListAsync();

        Assert.Equal("client-a", listing.Documents[0].Partition);
        Assert.Null(listing.Documents[1].Partition);
    }

    [Fact]
    public async Task PartitionEcho_ParsedFromSearchHits()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "q",
            results = new object[]
            {
                new { doc_id = "d1", score = 90, content_type = "text/plain", partition = "client-a" },
                new { doc_id = "d2", score = 80, content_type = "text/plain", partition = (string?)null },
            },
        });
        using var client = CreateClient(handler);

        var hits = await client.SearchAsync("q");

        Assert.Equal("client-a", hits[0].Partition);
        Assert.Null(hits[1].Partition);
    }

    [Fact]
    public async Task PartitionEcho_CarriedThroughRetrieve()
    {
        // Retrieve projects search hits into RetrievalResult; the echo must
        // survive the projection (content inlined so no download round-trip).
        var handler = MockHttpMessageHandler.WithJson(new
        {
            query = "q",
            results = new object[]
            {
                new { doc_id = "d1", score = 90, content_type = "text/plain", content = "full text", partition = "client-a" },
            },
        });
        using var client = CreateClient(handler);

        var results = await client.RetrieveAsync("q");

        var hit = Assert.Single(results);
        Assert.Equal("client-a", hit.Partition);
    }

    // ── typed partition_required exception ───────────────────────────

    [Fact]
    public async Task UnguardedByIdCallUnderStrictKey_ThrowsPartitionRequired()
    {
        // A key minted with strict scoping 400s any unguarded by-ID call; the
        // SDK surfaces the same typed exception as the unscoped read/write case.
        var handler = MockHttpMessageHandler.WithJson(
            new
            {
                error = "This API key requires every document call to name a partition.",
                code = "partition_required",
            },
            HttpStatusCode.BadRequest);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<PartitionRequiredException>(
            () => client.GetAsync("abc-123"));

        Assert.Equal("partition_required", ex.ErrorCode);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task UnscopedMultiTenantCall_ThrowsPartitionRequired()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new
            {
                error = "This API key is multi-tenant, so every search must name a partition.",
                code = "partition_required",
            },
            HttpStatusCode.BadRequest);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<PartitionRequiredException>(
            () => client.SearchAsync("anything"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("partition_required", ex.ErrorCode);
        Assert.False(ex.IsRetryable);
        Assert.IsAssignableFrom<AetherApiException>(ex);
    }

    [Fact]
    public void FromResponse_MapsPartitionRequiredFor400()
    {
        var ex = AetherApiException.FromResponse(
            HttpStatusCode.BadRequest, "must name a partition", "partition_required");

        Assert.IsType<PartitionRequiredException>(ex);
        Assert.IsAssignableFrom<AetherApiException>(ex);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("partition_required", ex.ErrorCode);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public void FromResponse_FallsBackForOther400Code()
    {
        // A different code on 400 stays the base type (e.g. plain invalid_input).
        var ex = AetherApiException.FromResponse(
            HttpStatusCode.BadRequest, "bad param", "invalid_input");

        Assert.Equal(typeof(AetherApiException), ex.GetType());
    }
}
