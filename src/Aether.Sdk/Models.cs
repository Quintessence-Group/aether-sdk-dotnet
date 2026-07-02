using System.Text.Json.Serialization;

namespace Aether.Sdk;

public class DocumentRecord
{
    [JsonPropertyName("doc_id")]
    public string DocId { get; set; } = "";

    [JsonPropertyName("cid")]
    public string Cid { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text/plain";

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("chunks")]
    public int Chunks { get; set; }

    [JsonPropertyName("vectors")]
    public int Vectors { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }

    /// <summary>The document's tags; empty when it has none.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = new List<string>();

    /// <summary>The document's source label, or null when it has none.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Structured metadata attached to the document.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public class SearchResult
{
    [JsonPropertyName("doc_id")]
    public string DocId { get; set; } = "";

    /// <summary>
    /// Calibrated relevance, 0–100 (higher = better); ~100 for a near-exact
    /// match. Computed server-side from the semantic similarity of the match.
    /// </summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text/plain";

    /// <summary>
    /// The specific passage (chunk) that matched the query. Fetch the full
    /// document text with <see cref="AetherClient.DownloadAsync"/> rather than
    /// inlining it — search never returns full document content.
    /// </summary>
    [JsonPropertyName("passage")]
    public string? Passage { get; set; }

    /// <summary>
    /// The entity (subject) this document was written under, if any. The engine
    /// emits <c>entity_id</c> on every search hit; mirrors <see cref="DocumentRecord.EntityId"/>.
    /// </summary>
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }

    /// <summary>The matched document's tags; empty when it has none.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = new List<string>();

    /// <summary>The matched document's source label, or null when it has none.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Structured metadata attached to the matched document.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();

    /// <summary>RFC 3339 creation timestamp of the matched document, unparsed; or null.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>RFC 3339 timestamp of the matched document's last update, unparsed; or
    /// null if it has never been updated since insert. Lets a caller spot a
    /// freshly-superseded hit without a second <c>Get</c> round-trip.</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    /// <summary>Feedback handle for the search that returned this hit. Present only
    /// when usage-feedback capture is enabled for your tenant (null otherwise); pass
    /// it to <see cref="AetherClient.SendSearchFeedbackAsync"/> together with this
    /// hit's <see cref="DocId"/>.</summary>
    [JsonPropertyName("query_id")]
    public string? QueryId { get; set; }
}

public class NodeStatus
{
    [JsonPropertyName("node_id")]
    public int NodeId { get; set; }

    [JsonPropertyName("documents")]
    public int Documents { get; set; }

    [JsonPropertyName("vectors")]
    public int Vectors { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Live cost-per-GiB for permanent archive uploads via Arweave/Irys, returned
/// by <see cref="AetherClient.GetArchivePriceAsync"/>. Mirrors the gateway's
/// 5-minute cached upstream price; values older than <see cref="CacheTtlSeconds"/>
/// trigger an upstream refresh on the next request.
/// </summary>
public class ArchivePrice
{
    /// <summary>Upstream archive network ("arweave", "irys").</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    /// <summary>
    /// Upload cost per GiB in US-cents at the moment <see cref="FetchedAt"/>
    /// was set. Used both for Portal display and for the at-time price stamped
    /// onto archive_events when bytes are uploaded.
    /// </summary>
    [JsonPropertyName("unit_price_cents_per_gib")]
    public long UnitPriceCentsPerGib { get; set; }

    /// <summary>RFC-3339 timestamp at which the gateway refreshed this value from upstream.</summary>
    [JsonPropertyName("fetched_at")]
    public string FetchedAt { get; set; } = "";

    /// <summary>Lifetime of the gateway's in-memory cache for this row.</summary>
    [JsonPropertyName("cache_ttl_seconds")]
    public ulong CacheTtlSeconds { get; set; }
}

/// <summary>Extends SearchResult with full document content for RAG workflows.</summary>
public class RetrievalResult
{
    [JsonPropertyName("doc_id")]
    public string DocId { get; set; } = "";

    /// <summary>Calibrated relevance, 0–100 (higher = better). See <see cref="SearchResult.Score"/>.</summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// <summary>Full document content as text, for use in RAG prompts.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text/plain";

    [JsonPropertyName("passage")]
    public string? Passage { get; set; }

    /// <summary>
    /// The entity (subject) this document was written under, if any. See
    /// <see cref="SearchResult.EntityId"/>.
    /// </summary>
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }

    /// <summary>The matched document's tags; empty when it has none.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = new List<string>();

    /// <summary>The matched document's source label, or null when it has none.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Structured metadata attached to the matched document.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();

    /// <summary>RFC 3339 creation timestamp of the matched document, unparsed; or null.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>RFC 3339 timestamp of the matched document's last update, unparsed; or
    /// null if it has never been updated since insert.</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

/// <summary>A text passage with its precomputed embedding vector.</summary>
public class EmbedPassage
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();
}

/// <summary>Result of a paginated document list operation.</summary>
public class DocumentListResult
{
    /// <summary>Documents in the current page.</summary>
    public List<DocumentRecord> Documents { get; set; } = new();

    /// <summary>Total number of active documents (across all pages).</summary>
    public int Total { get; set; }

    /// <summary>Whether more documents exist beyond this page.</summary>
    public bool HasMore { get; set; }
}

/// <summary>
/// Outcome of ingesting a single file via <see cref="AetherClient.IngestFilesAsync"/> /
/// <see cref="AetherClient.IngestDirectoryAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is one of:
/// <list type="bullet">
/// <item><description><c>"ingested"</c> — stored and indexed; <see cref="DocId"/> is set.</description></item>
/// <item><description><c>"skipped"</c> — the engine could not ingest this file (an unsupported or
/// binary type, one that needs the server-side document parser when it is not configured, or a file
/// over the size limit — HTTP 413/415/422). <see cref="Error"/> explains why. This is the graceful
/// path: the batch continues.</description></item>
/// <item><description><c>"error"</c> — an unexpected failure (e.g. the file could not be read, or a
/// transient API/network error). <see cref="Error"/> carries the detail.</description></item>
/// </list>
/// </remarks>
public class IngestResult
{
    /// <summary>The input path this result is for.</summary>
    public string Path { get; set; } = "";

    /// <summary><c>"ingested"</c>, <c>"skipped"</c>, or <c>"error"</c>.</summary>
    public string Status { get; set; } = "";

    /// <summary>The new document's id when <see cref="Status"/> is <c>"ingested"</c>; otherwise null.</summary>
    public string? DocId { get; set; }

    /// <summary>The content type resolved from the file extension, or null when it could not be resolved.</summary>
    public string? ContentType { get; set; }

    /// <summary>Explanation when <see cref="Status"/> is <c>"skipped"</c> or <c>"error"</c>; otherwise null.</summary>
    public string? Error { get; set; }
}

/// <summary>Configures chunking behavior for document processing.</summary>
public class ChunkingConfig
{
    /// <summary>Maximum size of each chunk in characters. 0 = server default.</summary>
    public int ChunkSize { get; set; }

    /// <summary>Number of overlapping characters between chunks. 0 = server default.</summary>
    public int Overlap { get; set; }
}

/// <summary>A document in a batch insert request.</summary>
public class BatchInsertItem
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    /// <summary>Tags for this item. Sent as a comma-joined string on the wire
    /// (the server item field is a string, not an array), the same convention as
    /// the document insert routes.</summary>
    [JsonIgnore]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TagsCsv => Tags is { Count: > 0 } ? string.Join(",", Tags) : null;

    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }

    /// <summary>Optional source label for this item; omitted from the wire when null.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// Partition this item is scoped to. Set automatically by a partition handle
    /// (<see cref="AetherClient.Partition"/>); the setter is internal so the
    /// handle is the only way to scope a partition. Null for the default partition.
    /// </summary>
    [JsonPropertyName("partition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Partition { get; internal set; }
}

/// <summary>A query in a batch search request.</summary>
public class BatchSearchQuery
{
    [JsonPropertyName("q")]
    public string Q { get; set; } = "";

    [JsonPropertyName("k")]
    public int K { get; set; } = 10;

    /// <summary>AND filter over tags; only hits carrying every one of these tags qualify.
    /// Sent as a comma-joined string on the wire, matching the per-query batch contract.</summary>
    [JsonIgnore]
    public List<string>? Tags { get; set; }

    /// <summary>OR-list filter over tags; a hit matching any of these tags qualifies.
    /// Sent as a comma-joined string on the wire, the same convention as <see cref="Tags"/>.</summary>
    [JsonIgnore]
    public List<string>? AnyTags { get; set; }

    /// <summary>OR-list filter over content types. Sent as a comma-joined string on the wire.</summary>
    [JsonIgnore]
    public List<string>? ContentTypes { get; set; }

    /// <summary>OR-list filter over source labels. Sent as a comma-joined string on the wire.</summary>
    [JsonIgnore]
    public List<string>? Sources { get; set; }

    // Wire fields: the metadata filters are sent as comma-joined strings (one
    // field per name), matching the engine's per-query batch contract.
    // Omitted when the corresponding list is null or empty.
    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TagsCsv => Tags is { Count: > 0 } ? string.Join(",", Tags) : null;

    [JsonPropertyName("any_tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnyTagsCsv => AnyTags is { Count: > 0 } ? string.Join(",", AnyTags) : null;

    [JsonPropertyName("content_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentTypesCsv => ContentTypes is { Count: > 0 } ? string.Join(",", ContentTypes) : null;

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcesCsv => Sources is { Count: > 0 } ? string.Join(",", Sources) : null;

    /// <summary>Structured metadata filter. Keys may be <c>metadata.&lt;key&gt;</c> or bare keys.</summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Filter { get; set; }

    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }

    [JsonPropertyName("since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Since { get; set; }

    [JsonPropertyName("until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Until { get; set; }

    [JsonPropertyName("last_n_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastNDays { get; set; }

    [JsonPropertyName("max_distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MaxDistance { get; set; }

    /// <summary>Blend recency into ranking, 0–1; omitted from the wire when null. See <see cref="AetherClient.SearchAsync"/>.</summary>
    [JsonPropertyName("recency_weight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RecencyWeight { get; set; }

    /// <summary>Recency decay half-life in days, &gt; 0; omitted from the wire when null. See <see cref="AetherClient.SearchAsync"/>.</summary>
    [JsonPropertyName("half_life_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HalfLifeDays { get; set; }

    /// <summary>Blend freshness into ranking, 0–1, boosting recently updated documents (<c>updated_at</c>, falling back to <c>created_at</c>); omitted from the wire when null. Composes with <see cref="RecencyWeight"/>; the server rejects a combined weight above 1. May require a Scale plan or higher. See <see cref="AetherClient.SearchAsync"/>.</summary>
    [JsonPropertyName("freshness_weight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreshnessWeight { get; set; }

    /// <summary>Freshness decay half-life in days, &gt; 0 (server default 14); omitted from the wire when null. See <see cref="AetherClient.SearchAsync"/>.</summary>
    [JsonPropertyName("freshness_half_life_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreshnessHalfLifeDays { get; set; }

    /// <summary>
    /// Partition this query is scoped to. Set automatically by a partition handle
    /// (<see cref="AetherClient.Partition"/>); the setter is internal so the
    /// handle is the only way to scope a partition. Null for the default partition.
    /// </summary>
    [JsonPropertyName("partition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Partition { get; internal set; }
}

/// <summary>Results for a single query in a batch search.</summary>
public class BatchSearchResponse
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("results")]
    public List<SearchResult> Results { get; set; } = new();
}

/// <summary>Response from an asynchronous document insertion.</summary>
public class AsyncJobResult
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("poll_url")]
    public string PollUrl { get; set; } = "";
}

/// <summary>Status of a background processing job.</summary>
public class JobStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("doc_id")]
    public string? DocId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Tallies returned by <see cref="AetherClient.BackfillEntityFromTagsAsync"/>,
/// reporting how the tenant's documents were affected by an entity-ID backfill.</summary>
public class EntityBackfillReport
{
    /// <summary>Number of active documents examined.</summary>
    [JsonPropertyName("scanned")]
    public int Scanned { get; set; }

    /// <summary>Number of documents whose entity ID was set from a matching tag.</summary>
    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    /// <summary>Documents skipped because they already had an entity ID (and overwrite was false).</summary>
    [JsonPropertyName("skipped_existing")]
    public int SkippedExisting { get; set; }

    /// <summary>Documents skipped because no tag matched the prefix.</summary>
    [JsonPropertyName("skipped_no_match")]
    public int SkippedNoMatch { get; set; }

    /// <summary>Documents skipped because two or more tags matched the prefix.</summary>
    [JsonPropertyName("skipped_ambiguous")]
    public int SkippedAmbiguous { get; set; }

    /// <summary>Documents skipped because the derived entity ID was invalid.</summary>
    [JsonPropertyName("skipped_invalid")]
    public int SkippedInvalid { get; set; }
}

// ── Partition lifecycle ──────────────────────────────────────────────

/// <summary>A partition and its active (non-tombstoned) document count,
/// returned by <see cref="AetherClient.ListPartitionsAsync"/>.</summary>
public class PartitionInfo
{
    /// <summary>The partition identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Number of active documents in the partition.</summary>
    [JsonPropertyName("document_count")]
    public int DocumentCount { get; set; }
}

/// <summary>An advisory flag about a likely-mistyped or abandoned partition,
/// surfaced by <see cref="AetherClient.ListPartitionsAsync"/>.</summary>
/// <remarks>
/// <see cref="Kind"/> is <c>single_document</c> (a partition holding one
/// document — often a typo or abandoned ghost) or <c>near_duplicate</c> (two
/// ids that differ only cosmetically — likely the same end-client under two
/// keys). Advisory only: a partition is never blocked from being created.
/// </remarks>
public class PartitionWarning
{
    /// <summary>The advisory category (<c>single_document</c> or <c>near_duplicate</c>).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The partition id(s) the warning concerns.</summary>
    [JsonPropertyName("partitions")]
    public IReadOnlyList<string> Partitions { get; set; } = new List<string>();

    /// <summary>Human-readable explanation of the warning.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
}

/// <summary>Result of <see cref="AetherClient.ListPartitionsAsync"/>: the tenant's
/// partitions plus advisory warnings. The default (unkeyed) partition is not listed.</summary>
public class PartitionList
{
    /// <summary>The tenant's partitions, ascending by id.</summary>
    [JsonPropertyName("partitions")]
    public IReadOnlyList<PartitionInfo> Partitions { get; set; } = new List<PartitionInfo>();

    /// <summary>Advisory warnings about likely typos or ghost partitions; possibly empty.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<PartitionWarning> Warnings { get; set; } = new List<PartitionWarning>();
}

// ── Provable isolation ───────────────────────────────────────────────

/// <summary>Evidence of which partition(s) a search actually touched, returned
/// alongside the results by <see cref="AetherClient.SearchTraceAsync"/>.</summary>
/// <remarks>
/// The trace is computed from the records actually returned, so it is evidence
/// — not intent. For a scoped query, <see cref="PartitionsTouched"/> is always
/// empty or exactly <c>[ScopedTo]</c>, and <see cref="CandidatesInScope"/> is the
/// partition's own size (proof the scope bounded the search as a hard ceiling, not
/// a post-filter). <see cref="Boundary"/> is <c>partition</c> (scoped) or
/// <c>tenant</c> (unscoped).
/// </remarks>
public class SearchTrace
{
    /// <summary>The partition the query was scoped to, or null when unscoped.</summary>
    [JsonPropertyName("scoped_to")]
    public string? ScopedTo { get; set; }

    /// <summary>The partition id(s) the returned records actually belong to.</summary>
    [JsonPropertyName("partitions_touched")]
    public IReadOnlyList<string> PartitionsTouched { get; set; } = new List<string>();

    /// <summary>Whether any returned record belonged to the default (unkeyed) partition.</summary>
    [JsonPropertyName("default_partition_touched")]
    public bool DefaultPartitionTouched { get; set; }

    /// <summary>Number of results returned.</summary>
    [JsonPropertyName("results")]
    public int Results { get; set; }

    /// <summary>The number of records the scope made eligible for matching, or null.</summary>
    [JsonPropertyName("candidates_in_scope")]
    public int? CandidatesInScope { get; set; }

    /// <summary>The enforced boundary: <c>partition</c> (scoped) or <c>tenant</c> (unscoped).</summary>
    [JsonPropertyName("boundary")]
    public string Boundary { get; set; } = "";
}

/// <summary>Search results plus the isolation <see cref="SearchTrace"/> that
/// produced them, returned by <see cref="AetherClient.SearchTraceAsync"/>.</summary>
public class TracedSearch
{
    /// <summary>The matched results (parsed exactly like <see cref="AetherClient.SearchAsync"/>).</summary>
    public IReadOnlyList<SearchResult> Results { get; set; } = new List<SearchResult>();

    /// <summary>The trace evidence for this search.</summary>
    public SearchTrace Trace { get; set; } = new();
}

/// <summary>Outcome of <see cref="AetherClient.VerifyIsolationAsync"/> on a scoped
/// handle: a one-line, assertable proof that a scoped search stayed in its partition.</summary>
/// <remarks>
/// <see cref="Ok"/> is true iff no returned record left the handle's partition.
/// Only meaningful for a query that returns results — a 0-result query passes
/// vacuously (<see cref="Results"/> is 0).
/// </remarks>
public class IsolationCheck
{
    /// <summary>True iff nothing leaked out of the handle's partition.</summary>
    public bool Ok { get; set; }

    /// <summary>The partition the handle was scoped to.</summary>
    public string? ScopedTo { get; set; }

    /// <summary>The partition id(s) the returned records belonged to.</summary>
    public IReadOnlyList<string> PartitionsTouched { get; set; } = new List<string>();

    /// <summary>Number of results returned.</summary>
    public int Results { get; set; }

    /// <summary>The number of records the scope made eligible for matching, or null.</summary>
    public int? CandidatesInScope { get; set; }

    /// <summary>Touched partitions other than the handle's — empty when isolation held.</summary>
    public IReadOnlyList<string> Leaked { get; set; } = new List<string>();
}

// Internal response wrappers (not exported)
internal class DocumentListResponse
{
    [JsonPropertyName("documents")]
    public List<DocumentRecord> Documents { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
}

internal class SearchResponse
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("results")]
    public List<SearchResult> Results { get; set; } = new();

    // Response-level usage-feedback handle; the SDK stamps it onto every hit.
    // Null unless feedback capture is enabled for the tenant.
    [JsonPropertyName("query_id")]
    public string? QueryId { get; set; }
}

// Trace-search envelope: the normal {query, results} plus the isolation trace.
// Results deserialize through the same SearchResult model the bare search uses.
internal class TracedSearchResponse
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("results")]
    public List<SearchResult> Results { get; set; } = new();

    [JsonPropertyName("query_id")]
    public string? QueryId { get; set; }

    [JsonPropertyName("trace")]
    public SearchTrace Trace { get; set; } = new();
}

// Wire shape of DELETE /partitions/{id}: only documents_deleted is surfaced.
internal class DeletePartitionResponse
{
    [JsonPropertyName("documents_deleted")]
    public int DocumentsDeleted { get; set; }
}

public class InsertWithEmbeddingsRequest
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("passages")]
    public List<EmbedPassage>? Passages { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }

    /// <summary>Optional source label for this document; omitted from the wire when null.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// Partition this document is scoped to. Set automatically by a partition handle
    /// (<see cref="AetherClient.Partition"/>); the setter is internal so the
    /// handle is the only way to scope a partition. Null for the default partition.
    /// </summary>
    [JsonPropertyName("partition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Partition { get; internal set; }
}

internal class VectorSearchRequest
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();

    [JsonPropertyName("k")]
    public int K { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    // OR-list filters are sent as JSON arrays on this endpoint.
    [JsonPropertyName("any_tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AnyTags { get; set; }

    [JsonPropertyName("content_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ContentTypes { get; set; }

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Sources { get; set; }

    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object?>? Filter { get; set; }

    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }

    [JsonPropertyName("since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Since { get; set; }

    [JsonPropertyName("until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Until { get; set; }

    [JsonPropertyName("last_n_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastNDays { get; set; }

    [JsonPropertyName("max_distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MaxDistance { get; set; }

    [JsonPropertyName("recency_weight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RecencyWeight { get; set; }

    [JsonPropertyName("half_life_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HalfLifeDays { get; set; }

    [JsonPropertyName("freshness_weight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreshnessWeight { get; set; }

    [JsonPropertyName("freshness_half_life_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FreshnessHalfLifeDays { get; set; }

    [JsonPropertyName("partition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Partition { get; set; }
}

internal class BatchInsertRequest
{
    [JsonPropertyName("documents")]
    public List<BatchInsertItem> Documents { get; set; } = new();

    [JsonPropertyName("chunk_size")]
    public int? ChunkSize { get; set; }

    [JsonPropertyName("overlap")]
    public int? Overlap { get; set; }
}

internal class BatchInsertResponse
{
    [JsonPropertyName("results")]
    public List<DocumentRecord> Results { get; set; } = new();
}

internal class BatchSearchRequest
{
    [JsonPropertyName("queries")]
    public List<BatchSearchQuery> Queries { get; set; } = new();
}

internal class EntityBackfillRequest
{
    [JsonPropertyName("tag_prefix")]
    public string TagPrefix { get; set; } = "";

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; }
}

// Wire shape of one query's results in a batch search: the public
// BatchSearchResponse envelope plus the optional per-query usage-feedback
// query_id, which the SDK stamps onto each hit.
internal class BatchSearchResponseItemWire
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("results")]
    public List<SearchResult> Results { get; set; } = new();

    [JsonPropertyName("query_id")]
    public string? QueryId { get; set; }
}

internal class BatchSearchResponseWrapper
{
    [JsonPropertyName("results")]
    public List<BatchSearchResponseItemWire> Results { get; set; } = new();
}

// Wire body of POST /search/feedback.
internal class SearchFeedbackRequest
{
    [JsonPropertyName("query_id")]
    public string QueryId { get; set; } = "";

    [JsonPropertyName("doc_id")]
    public string DocId { get; set; } = "";

    [JsonPropertyName("signal")]
    public string Signal { get; set; } = "";
}

// Wire shape of the POST /search/feedback ack ({"recorded": true}).
internal class SearchFeedbackResponse
{
    [JsonPropertyName("recorded")]
    public bool Recorded { get; set; }
}

internal class ErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

// ── Memory graph result types ────────────────────────────────────────────
//
// Read models mirroring the engine's /memory/* response DTOs 1:1. Field names
// are wire snake_case via [JsonPropertyName] (the shared JsonSerializerOptions
// has no naming policy, so every property needs an explicit mapping — same
// pattern as DocumentRecord). `attributes` and a fact `value` are scalar JSON
// (string | number | bool | null) surfaced as object?; timestamps are RFC 3339
// strings, left unparsed.

/// <summary>A typed node in the owner's memory graph (<c>/memory/entities</c>).</summary>
public class MemoryEntity
{
    /// <summary>Engine-minted unless you supply one (idempotency key).</summary>
    [JsonPropertyName("memory_entity_id")]
    public string MemoryEntityId { get; set; } = "";

    /// <summary>The owner scope (= the Memory's entity ID).</summary>
    [JsonPropertyName("entity_id")]
    public string EntityId { get; set; } = "";

    /// <summary>The scope's partition, or null.</summary>
    [JsonPropertyName("partition")]
    public string? Partition { get; set; }

    /// <summary>Caller-controlled type (<c>person</c>, <c>project</c>, <c>preference</c>, …).</summary>
    [JsonPropertyName("entity_type")]
    public string EntityType { get; set; } = "";

    /// <summary>Optional label.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>Possibly empty.</summary>
    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    /// <summary>Scalar attribute map; possibly empty.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; set; } = new();

    /// <summary>RFC 3339 creation timestamp, unparsed.</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    /// <summary>RFC 3339 update timestamp, unparsed.</summary>
    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>A directed, typed edge between two entities (<c>/memory/relationships</c>).</summary>
public class MemoryRelationship
{
    /// <summary>Engine-minted unless supplied.</summary>
    [JsonPropertyName("relationship_id")]
    public string RelationshipId { get; set; } = "";

    /// <summary>Owner scope.</summary>
    [JsonPropertyName("entity_id")]
    public string EntityId { get; set; } = "";

    /// <summary>The scope's partition, or null.</summary>
    [JsonPropertyName("partition")]
    public string? Partition { get; set; }

    /// <summary>Source node id (<c>memory_entity_id</c>). Edges are directional.</summary>
    [JsonPropertyName("from_entity_id")]
    public string FromEntityId { get; set; } = "";

    /// <summary>Target node id (<c>memory_entity_id</c>).</summary>
    [JsonPropertyName("to_entity_id")]
    public string ToEntityId { get; set; } = "";

    /// <summary>Caller-controlled type (<c>works_at</c>, <c>owns</c>, <c>prefers</c>, …).</summary>
    [JsonPropertyName("relationship_type")]
    public string RelationshipType { get; set; } = "";

    /// <summary>Scalar attribute map; possibly empty.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; set; } = new();

    /// <summary>When it became true, if known (RFC 3339).</summary>
    [JsonPropertyName("valid_from")]
    public string? ValidFrom { get; set; }

    /// <summary>When Aether ingested it (RFC 3339).</summary>
    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; set; } = "";

    /// <summary>Null while active; set when retracted/superseded.</summary>
    [JsonPropertyName("invalid_from")]
    public string? InvalidFrom { get; set; }

    /// <summary>RFC 3339 creation timestamp, unparsed.</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    /// <summary>RFC 3339 update timestamp, unparsed.</summary>
    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>A temporal assertion with contradiction-resolution history (<c>/memory/facts</c>).</summary>
public class MemoryFact
{
    /// <summary>Engine-minted.</summary>
    [JsonPropertyName("fact_id")]
    public string FactId { get; set; } = "";

    /// <summary>Owner scope.</summary>
    [JsonPropertyName("entity_id")]
    public string EntityId { get; set; } = "";

    /// <summary>The scope's partition, or null.</summary>
    [JsonPropertyName("partition")]
    public string? Partition { get; set; }

    /// <summary><c>owner</c> | <c>entity</c> | <c>relationship</c>.</summary>
    [JsonPropertyName("subject_type")]
    public string SubjectType { get; set; } = "";

    /// <summary>Null for <c>owner</c>; the node/edge id otherwise.</summary>
    [JsonPropertyName("subject_id")]
    public string? SubjectId { get; set; }

    /// <summary>Caller-controlled (<c>favorite_color</c>, <c>status</c>, …).</summary>
    [JsonPropertyName("predicate")]
    public string Predicate { get; set; } = "";

    /// <summary>Scalar value: string | number | bool | null.</summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    /// <summary><c>single</c> (default) or <c>multi</c>.</summary>
    [JsonPropertyName("cardinality")]
    public string Cardinality { get; set; } = "single";

    /// <summary>Semantic effective time, if known (RFC 3339).</summary>
    [JsonPropertyName("valid_from")]
    public string? ValidFrom { get; set; }

    /// <summary>Ingest time (RFC 3339).</summary>
    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; set; } = "";

    /// <summary>Null while active; set when superseded/retracted.</summary>
    [JsonPropertyName("invalid_from")]
    public string? InvalidFrom { get; set; }

    /// <summary>The prior active fact this one replaced.</summary>
    [JsonPropertyName("supersedes_fact_id")]
    public string? SupersedesFactId { get; set; }

    /// <summary>RFC 3339 creation timestamp, unparsed.</summary>
    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = "";

    /// <summary>RFC 3339 update timestamp, unparsed.</summary>
    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>Report returned by <c>consolidate</c> (<c>POST /memory/consolidate</c>).</summary>
public class ConsolidationReport
{
    /// <summary>Active facts in scope before consolidation.</summary>
    [JsonPropertyName("active_facts_before")]
    public int ActiveFactsBefore { get; set; }

    /// <summary>Active facts remaining after.</summary>
    [JsonPropertyName("active_facts_after")]
    public int ActiveFactsAfter { get; set; }

    /// <summary>Redundant facts soft-retracted (kept in history).</summary>
    [JsonPropertyName("retracted")]
    public int Retracted { get; set; }
}

// Internal list-envelope wrappers (the `count` echo is dropped — callers use the
// list length, mirroring how the document list drops its `documents` envelope).
internal class MemoryEntityListResponse
{
    [JsonPropertyName("entities")]
    public List<MemoryEntity> Entities { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

internal class MemoryRelationshipListResponse
{
    [JsonPropertyName("relationships")]
    public List<MemoryRelationship> Relationships { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

internal class MemoryFactListResponse
{
    [JsonPropertyName("facts")]
    public List<MemoryFact> Facts { get; set; } = new();

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
