using System.Net;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Structured-query + field-schema parity (AGENTS.md four-SDK rule): the
/// <c>QueryAsync</c> Mode A / Mode B dispatch, the <c>Schema</c> declare / list /
/// delete facade, partition scoping, and typed error mapping — each driven through
/// a real client over the mocked transport so the genuine request / parse path runs.
/// </summary>
public class StructuredQueryTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, "http://localhost:9000");
    }

    [Fact]
    public async Task QueryAsync_ModeA_ReturnsPageAndPostsBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = new[] { new { doc_id = "d1", content_type = "text/plain" } },
            total = 1,
            has_more = false,
        });

        using var client = CreateClient(handler);
        var result = await client.QueryAsync(new QueryRequest
        {
            Filter = new Dictionary<string, object?> { ["field"] = "status", ["op"] = "eq", ["value"] = "paid" },
            Sort = new List<QuerySort> { new() { By = "created_at", Dir = "desc" } },
            Limit = 10,
        });

        Assert.False(result.IsAggregate);
        Assert.NotNull(result.Page);
        Assert.Single(result.Page!.Documents);
        Assert.Equal("d1", result.Page.Documents[0].DocId);
        Assert.Equal(1, result.Page.Total);
        Assert.False(result.Page.HasMore);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v1/query", handler.LastRequest.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.TryGetProperty("filter", out _));
        Assert.True(body.RootElement.TryGetProperty("sort", out _));
        Assert.False(body.RootElement.TryGetProperty("aggregate", out _));
        Assert.False(body.RootElement.TryGetProperty("partition", out _));
    }

    [Fact]
    public async Task QueryAsync_ModeB_ReturnsAggregate()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            groups = new[]
            {
                new { keys = new { status = "paid" }, aggregates = new { total = 3 } },
            },
            total_groups = 1,
            scanned = 12,
        });

        using var client = CreateClient(handler);
        var result = await client.QueryAsync(new QueryRequest
        {
            GroupBy = new List<string> { "status" },
            Aggregate = new List<Dictionary<string, object?>>
            {
                new() { ["op"] = "count", ["as"] = "total" },
            },
        });

        Assert.True(result.IsAggregate);
        Assert.NotNull(result.Aggregate);
        Assert.Single(result.Aggregate!.Groups);
        Assert.Equal(1, result.Aggregate.TotalGroups);
        Assert.Equal(12, result.Aggregate.Scanned);

        using var reqBody = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(reqBody.RootElement.TryGetProperty("aggregate", out _));
        Assert.True(reqBody.RootElement.TryGetProperty("group_by", out _));
    }

    [Fact]
    public async Task Schema_DeclareFieldsAsync_PutsFieldsAndParsesStats()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            fields = new[]
            {
                new { name = "amount", type = "int", source = new { metadata = "amount" },
                    coverage = 2, mismatch_count = 1, backfill = "complete" },
            },
        });

        using var client = CreateClient(handler);
        var fields = await client.Schema.DeclareFieldsAsync(new[]
        {
            new FieldInput { Name = "amount", Type = "int", Source = new() { ["metadata"] = "amount" } },
        });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("/v1/schema/fields", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Single(fields);
        Assert.Equal("amount", fields[0].Name);
        Assert.Equal("int", fields[0].Type);
        Assert.Equal(2, fields[0].Coverage);
        Assert.Equal(1, fields[0].MismatchCount);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.TryGetProperty("fields", out var f));
        Assert.Equal("amount", f[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Schema_ListFieldsAsync_GetsAndReturnsFields()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            fields = new[] { new { name = "amount", type = "int" } },
        });

        using var client = CreateClient(handler);
        var fields = await client.Schema.ListFieldsAsync();

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v1/schema/fields", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Single(fields);
        Assert.Equal("amount", fields[0].Name);
    }

    [Fact]
    public async Task Schema_DeleteFieldAsync_DeletesAndReturnsRemaining()
    {
        var handler = MockHttpMessageHandler.WithJson(new { fields = Array.Empty<object>() });

        using var client = CreateClient(handler);
        var remaining = await client.Schema.DeleteFieldAsync("amount");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/v1/schema/fields/amount", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task QueryOnScopedHandle_PinsPartitionInBody()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            documents = Array.Empty<object>(),
            total = 0,
            has_more = false,
        });

        using var client = CreateClient(handler);
        await client.Partition("client-a").QueryAsync(new QueryRequest());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("client-a", body.RootElement.GetProperty("partition").GetString());
    }

    [Fact]
    public async Task SchemaOnScopedHandle_PinsPartitionQueryParam()
    {
        var handler = MockHttpMessageHandler.WithJson(new { fields = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await client.Partition("client-a").Schema.ListFieldsAsync();

        Assert.Contains("partition=client-a", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task QueryAsync_MapsTypedError()
    {
        var handler = MockHttpMessageHandler.WithJson(
            new { error = "partition required", code = "partition_required" },
            HttpStatusCode.BadRequest);

        using var client = CreateClient(handler);
        var ex = await Assert.ThrowsAsync<PartitionRequiredException>(
            () => client.QueryAsync(new QueryRequest()));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("partition_required", ex.ErrorCode);
    }

    [Fact]
    public async Task Schema_DeleteFieldAsync_ValidatesName()
    {
        var handler = MockHttpMessageHandler.WithJson(new { fields = Array.Empty<object>() });

        using var client = CreateClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.Schema.DeleteFieldAsync(""));
        Assert.Null(handler.LastRequest);
    }
}
