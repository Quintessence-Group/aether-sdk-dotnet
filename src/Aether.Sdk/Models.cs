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
    /// match. Computed server-side as <c>round(100 * (1 - cosine_distance))</c>.
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

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Optional entity ID to associate with this document, for entity-scoped search and list filters.</summary>
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }
}

/// <summary>A query in a batch search request.</summary>
public class BatchSearchQuery
{
    [JsonPropertyName("q")]
    public string Q { get; set; } = "";

    [JsonPropertyName("k")]
    public int K { get; set; } = 10;

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Only match documents with this entity ID.</summary>
    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }

    /// <summary>Only match documents created at or after this RFC 3339 timestamp (inclusive).</summary>
    [JsonPropertyName("since")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Since { get; set; }

    /// <summary>Only match documents created at or before this RFC 3339 timestamp (inclusive).</summary>
    [JsonPropertyName("until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Until { get; set; }

    /// <summary>Only match documents created in the last N days (UTC, server clock).</summary>
    [JsonPropertyName("last_n_days")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastNDays { get; set; }

    /// <summary>
    /// Optional cosine-distance ceiling. Results with
    /// <c>distance &gt; MaxDistance</c> are dropped server-side, after
    /// reranking. Leave null to return the top-k regardless of distance.
    /// </summary>
    [JsonPropertyName("max_distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MaxDistance { get; set; }
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

    /// <summary>Optional entity ID to associate with the document, for entity-scoped search and list filters.</summary>
    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }
}

internal class VectorSearchRequest
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();

    [JsonPropertyName("k")]
    public int K { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

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
}

internal class BatchInsertRequest
{
    [JsonPropertyName("documents")]
    public List<BatchInsertWireItem> Documents { get; set; } = new();

    [JsonPropertyName("chunk_size")]
    public int? ChunkSize { get; set; }

    [JsonPropertyName("overlap")]
    public int? Overlap { get; set; }
}

/// <summary>
/// Wire shape for a batch insert document. The prod batch deserializer expects
/// <c>tags</c> as a comma-joined string (matching every other endpoint), not a
/// JSON array — sending an array yields HTTP 422. We serialize the public
/// <see cref="BatchInsertItem.Tags"/> list down to a comma string here.
/// </summary>
internal class BatchInsertWireItem
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tags { get; set; }

    [JsonPropertyName("entity_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityId { get; set; }
}

/// <summary>Wire shape for a batch search query; <c>tags</c> is a comma-joined string.</summary>
internal class BatchSearchWireQuery
{
    [JsonPropertyName("q")]
    public string Q { get; set; } = "";

    [JsonPropertyName("k")]
    public int K { get; set; } = 10;

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tags { get; set; }

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
}

internal class BatchInsertResponse
{
    [JsonPropertyName("results")]
    public List<DocumentRecord> Results { get; set; } = new();
}

internal class BatchSearchRequest
{
    [JsonPropertyName("queries")]
    public List<BatchSearchWireQuery> Queries { get; set; } = new();
}

internal class EntityBackfillRequest
{
    [JsonPropertyName("tag_prefix")]
    public string TagPrefix { get; set; } = "";

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; set; }
}

internal class BatchSearchResponseWrapper
{
    [JsonPropertyName("results")]
    public List<BatchSearchResponse> Results { get; set; } = new();
}

internal class ErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
