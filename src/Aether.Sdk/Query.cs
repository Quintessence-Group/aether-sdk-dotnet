using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aether.Sdk;

/// <summary>A declared typed field for the structured-query layer, with its live
/// coverage / mismatch stats.</summary>
public class FieldSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>One of: string, int, float, bool, datetime, string_list.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Where the value comes from: {"metadata": "&lt;key&gt;"} or {"regex": "&lt;pattern&gt;"}.</summary>
    [JsonPropertyName("source")]
    public Dictionary<string, object?> Source { get; set; } = new();

    /// <summary>Hard-partition scope, or null for a tenant-wide field.</summary>
    [JsonPropertyName("partition_scope")]
    public string? PartitionScope { get; set; }

    /// <summary>Active documents whose source value coerced to the declared type.</summary>
    [JsonPropertyName("coverage")]
    public int Coverage { get; set; }

    /// <summary>Active documents whose source value was present but failed to coerce.</summary>
    [JsonPropertyName("mismatch_count")]
    public int MismatchCount { get; set; }

    /// <summary>Backfill state; "complete" in v1 (synchronous on declare).</summary>
    [JsonPropertyName("backfill")]
    public string Backfill { get; set; } = "complete";
}

/// <summary>Declares (or replaces) one typed field via
/// <see cref="AetherSchema.DeclareFieldsAsync"/>.</summary>
public class FieldInput
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>One of: string, int, float, bool, datetime, string_list.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>{"metadata": "&lt;key&gt;"} or {"regex": "&lt;pattern&gt;"}.</summary>
    [JsonPropertyName("source")]
    public Dictionary<string, object?> Source { get; set; } = new();

    [JsonPropertyName("partition_scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartitionScope { get; set; }
}

/// <summary>Orders a query by a field (Mode A) or an aggregate output / group key
/// (Mode B). <c>Dir</c> is "asc" or "desc"; absent values sort last.</summary>
public class QuerySort
{
    [JsonPropertyName("by")]
    public string By { get; set; } = "";

    [JsonPropertyName("dir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dir { get; set; }
}

/// <summary>The options for a structured analytical query
/// (<see cref="AetherClient.QueryAsync"/>). The presence of <see cref="Aggregate"/>
/// selects Mode B (aggregation); otherwise it is Mode A (a document page).</summary>
public class QueryRequest
{
    /// <summary>The unified filter grammar ({and|or|not} over {field, op, value}
    /// leaves) or the metadata shorthand map; null matches every doc in scope.</summary>
    public object? Filter { get; set; }

    /// <summary>Up to two fields to group by (Mode B).</summary>
    public List<string>? GroupBy { get; set; }

    /// <summary>{op, field?, as?} specs; its presence selects Mode B.</summary>
    public List<Dictionary<string, object?>>? Aggregate { get; set; }

    /// <summary>Ordering (a field in Mode A; an aggregate output or group key in Mode B).</summary>
    public List<QuerySort>? Sort { get; set; }

    /// <summary>Caps documents (Mode A, &lt;= 1000) or groups (Mode B).</summary>
    public int? Limit { get; set; }

    /// <summary>Skips documents (Mode A only).</summary>
    public int Offset { get; set; }

    /// <summary>Scopes the query; a partition-scoped client
    /// (<see cref="AetherClient.Partition"/>) overrides this.</summary>
    public string? Partition { get; set; }
}

/// <summary>The Mode A result: a page of matching documents plus the total matched
/// and whether more pages remain.</summary>
public class QueryPage
{
    [JsonPropertyName("documents")]
    public List<DocumentRecord> Documents { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

/// <summary>One group in a Mode B aggregation result.</summary>
public class QueryGroup
{
    /// <summary>Group-by values by field name; empty for a whole-population aggregate.</summary>
    [JsonPropertyName("keys")]
    public Dictionary<string, object?> Keys { get; set; } = new();

    /// <summary>Computed aggregates by output name (the "as" alias or a default).</summary>
    [JsonPropertyName("aggregates")]
    public Dictionary<string, object?> Aggregates { get; set; } = new();
}

/// <summary>The Mode B result: the matching documents grouped and folded into the
/// requested aggregates.</summary>
public class AggregateResult
{
    [JsonPropertyName("groups")]
    public List<QueryGroup> Groups { get; set; } = new();

    [JsonPropertyName("total_groups")]
    public int TotalGroups { get; set; }

    [JsonPropertyName("scanned")]
    public int Scanned { get; set; }
}

/// <summary>The result of <see cref="AetherClient.QueryAsync"/>: exactly one of
/// <see cref="Page"/> (Mode A) or <see cref="Aggregate"/> (Mode B) is set,
/// selected by whether the request carried an aggregate.</summary>
public sealed class QueryResult
{
    /// <summary>Set for a Mode A (no-aggregate) query.</summary>
    public QueryPage? Page { get; set; }

    /// <summary>Set for a Mode B (aggregation) query.</summary>
    public AggregateResult? Aggregate { get; set; }

    /// <summary>True when this is a Mode B aggregation result.</summary>
    public bool IsAggregate => Aggregate != null;
}

internal sealed class SchemaFieldsResponse
{
    [JsonPropertyName("fields")]
    public List<FieldSchema> Fields { get; set; } = new();
}

public partial class AetherClient
{
    private AetherSchema? _schema;

    /// <summary>The field-schema facade — declare / list / delete the typed fields
    /// that <see cref="QueryAsync"/> filters, sorts, and aggregates over. On a
    /// partition-scoped handle every call is pinned to that partition.</summary>
    public AetherSchema Schema => _schema ??= new AetherSchema(this);

    /// <summary>
    /// Runs a structured analytical query over the tenant's declared typed fields +
    /// the built-in record fields. Exact and deterministic — it never consults an
    /// embedding.
    /// </summary>
    /// <remarks>
    /// Mode A (<c>request.Aggregate</c> empty) returns <see cref="QueryResult.Page"/>,
    /// a paginated document page. Mode B (<c>request.Aggregate</c> set) returns
    /// <see cref="QueryResult.Aggregate"/>. Guardrail violations (the candidate-scan
    /// cap, the max-groups cap, an unknown field, a type-mismatched literal, or a
    /// non-numeric numeric aggregate) throw a 400 <see cref="AetherApiException"/>.
    /// </remarks>
    public async Task<QueryResult> QueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var body = new Dictionary<string, object?>();
        if (request.Filter != null) body["filter"] = request.Filter;
        if (request.GroupBy is { Count: > 0 }) body["group_by"] = request.GroupBy;
        if (request.Aggregate is { Count: > 0 }) body["aggregate"] = request.Aggregate;
        if (request.Sort is { Count: > 0 }) body["sort"] = request.Sort;
        if (request.Limit.HasValue) body["limit"] = request.Limit.Value;
        if (request.Offset != 0) body["offset"] = request.Offset;
        // A partition-scoped handle wins over an explicit request field.
        var scope = _partition ?? request.Partition;
        if (!string.IsNullOrEmpty(scope)) body["partition"] = scope;

        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        if (request.Aggregate is { Count: > 0 })
        {
            var agg = await RequestAsync<AggregateResult>(
                "/query", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
            return new QueryResult { Aggregate = agg };
        }

        var page = await RequestAsync<QueryPage>(
            "/query", HttpMethod.Post, content, cancellationToken).ConfigureAwait(false);
        return new QueryResult { Page = page };
    }

    internal async Task<List<FieldSchema>> SchemaDeclareAsync(
        IEnumerable<FieldInput> fields,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?> { ["fields"] = fields };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await RequestAsync<SchemaFieldsResponse>(
            AppendPartitionGuard("/schema/fields"), HttpMethod.Put, content, cancellationToken)
            .ConfigureAwait(false);
        return resp.Fields;
    }

    internal async Task<List<FieldSchema>> SchemaListAsync(CancellationToken cancellationToken)
    {
        var resp = await RequestAsync<SchemaFieldsResponse>(
            AppendPartitionGuard("/schema/fields"), HttpMethod.Get, null, cancellationToken)
            .ConfigureAwait(false);
        return resp.Fields;
    }

    internal async Task<List<FieldSchema>> SchemaDeleteAsync(string name, CancellationToken cancellationToken)
    {
        var resp = await RequestAsync<SchemaFieldsResponse>(
            AppendPartitionGuard($"/schema/fields/{Uri.EscapeDataString(name)}"),
            HttpMethod.Delete, null, cancellationToken).ConfigureAwait(false);
        return resp.Fields;
    }
}

/// <summary>The <c>client.Schema</c> facade over <c>/v1/schema/fields</c> — declare /
/// list / delete the typed fields the structured-query layer operates on. On a
/// partition-scoped client every call is pinned to that partition.</summary>
public sealed class AetherSchema
{
    private readonly AetherClient _client;

    internal AetherSchema(AetherClient client) => _client = client;

    /// <summary>Declares (or replaces) typed fields and returns the declared set.
    /// Re-declaring a name replaces its type/source and re-backfills.</summary>
    public Task<List<FieldSchema>> DeclareFieldsAsync(
        IEnumerable<FieldInput> fields,
        CancellationToken cancellationToken = default)
    {
        if (fields == null)
            throw new ArgumentNullException(nameof(fields));
        return _client.SchemaDeclareAsync(fields, cancellationToken);
    }

    /// <summary>Returns the tenant's declared fields with their live coverage /
    /// mismatch / backfill stats.</summary>
    public Task<List<FieldSchema>> ListFieldsAsync(CancellationToken cancellationToken = default)
        => _client.SchemaListAsync(cancellationToken);

    /// <summary>Removes a declared field and returns the remaining fields.</summary>
    public Task<List<FieldSchema>> DeleteFieldAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("field name cannot be empty", nameof(name));
        return _client.SchemaDeleteAsync(name, cancellationToken);
    }
}
