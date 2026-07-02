using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Partition lifecycle (list / delete), provable isolation (trace /
/// verify-isolation), and the partition_required typed exception. Each test
/// drives a real client over the mocked transport so the genuine request /
/// parse / error-mapping path runs (mirrors sdk/python/tests/test_partitions.py).
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

    // ── typed partition_required exception ───────────────────────────

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
