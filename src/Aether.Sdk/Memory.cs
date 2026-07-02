using System.Globalization;
using System.Text;

namespace Aether.Sdk;

/// <summary>
/// A single remembered item, returned by <see cref="Memory"/>'s
/// <see cref="Memory.RememberAsync"/>, <see cref="Memory.RecallAsync"/>, and
/// <see cref="Memory.ListAsync"/>.
/// </summary>
public class MemoryItem
{
    /// <summary>The underlying document ID.</summary>
    public string Id { get; set; } = "";

    /// <summary>The remembered text.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// RFC 3339 creation timestamp, unparsed. Populated by <c>remember</c> and
    /// <c>list</c>; on <c>recall</c> only when <c>recencyWeight &gt; 0</c>, else null.
    /// </summary>
    public string? CreatedAt { get; set; }

    /// <summary>The owning entity (always the Memory's entity ID).</summary>
    public string? EntityId { get; set; }

    /// <summary>Structured metadata attached to the memory.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();

    /// <summary>
    /// Relevance signal, higher = more relevant. Relative within a single
    /// <c>recall</c> call and not comparable across calls. Populated by
    /// <c>recall</c> only; null for <c>remember</c>/<c>list</c>.
    /// </summary>
    public double? Score { get; set; }
}

/// <summary>
/// Configuration options for <see cref="Memory"/>. Extends
/// <see cref="AetherClientOptions"/> with memory-specific knobs.
/// </summary>
public class MemoryOptions : AetherClientOptions
{
    /// <summary>
    /// Half-life for the recency decay used by <see cref="Memory.RecallAsync"/>
    /// when <c>recencyWeight &gt; 0</c>. At one half-life, the recency contribution
    /// is 0.5. Default: 30 days.
    /// </summary>
    public TimeSpan? HalfLife { get; set; }

    /// <summary>
    /// Enable server-side fact extraction for this Memory's
    /// <see cref="Memory.RememberAsync"/>: the text is distilled into atomic facts,
    /// each stored as a sibling <c>kind:fact</c> memory and recallable like any
    /// other. Requires fact extraction to be configured on the node. Default false.
    /// </summary>
    public bool ExtractFacts { get; set; }

    /// <summary>
    /// Clock used for the deterministic recency algorithm. A dependency-free
    /// delegate returning "now"; defaults to <c>() =&gt; DateTimeOffset.UtcNow</c>.
    /// Override in tests for determinism. A plain delegate is used (not
    /// <c>System.TimeProvider</c>) so no transitive package is forced on the
    /// <c>netstandard2.0</c> target.
    /// </summary>
    public Func<DateTimeOffset>? Clock { get; set; }
}

/// <summary>
/// Entity-scoped convenience facade over <see cref="AetherClient"/>. Construct it
/// once with an <c>entityId</c> (a user, customer, patient, or agent session) and
/// every call is automatically scoped to that entity.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Memory"/> <b>owns</b> a raw client (composition, not inheritance). It
/// adds no new HTTP routes and changes no existing raw-client behavior: all
/// transport, retry, error, and timeout semantics are inherited unchanged, and the
/// raw client's existing error types surface through it unmodified.
/// </para>
/// <para>
/// Metadata passed to <see cref="RememberAsync"/> is written as structured typed
/// document metadata and echoed by the raw document API.
/// </para>
/// </remarks>
public class Memory : IDisposable
{
    // Deterministic recency algorithm constants (shared across all four SDKs).
    private const int Overfetch = 4;
    private const int MaxCandidates = 100;

    private static readonly TimeSpan DefaultHalfLife = TimeSpan.FromDays(30);

    private readonly AetherClient _client;
    private readonly bool _ownsClient;
    private readonly string _entityId;
    private readonly TimeSpan _halfLife;
    private readonly bool _extractFacts;
    private readonly Func<DateTimeOffset> _clock;
    private bool _disposed;

    /// <summary>The entity ID every operation is scoped to. Fixed at construction.</summary>
    public string EntityId => _entityId;

    /// <summary>
    /// Creates a memory for <paramref name="entityId"/>, building its own
    /// <see cref="AetherClient"/> from <paramref name="options"/> (connection
    /// options resolve the same way as the raw client).
    /// </summary>
    /// <param name="entityId">The entity to scope to. Non-empty, 1–256 characters.</param>
    /// <param name="options">Optional connection and memory options.</param>
    /// <exception cref="ArgumentException">The entity ID is empty or longer than 256 characters.</exception>
    public Memory(string entityId, MemoryOptions? options = null)
        : this(entityId, new AetherClient(options), ownsClient: true, options)
    {
    }

    /// <summary>
    /// Creates a memory for <paramref name="entityId"/> around an already-built
    /// <paramref name="client"/> (dependency injection). The caller retains
    /// ownership of <paramref name="client"/>: disposing this <see cref="Memory"/>
    /// does not dispose the injected client.
    /// </summary>
    /// <param name="entityId">The entity to scope to. Non-empty, 1–256 characters.</param>
    /// <param name="client">An existing raw client to wrap.</param>
    /// <param name="options">Optional memory options (connection fields are ignored — the client is already built).</param>
    /// <exception cref="ArgumentException">The entity ID is empty or longer than 256 characters.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    public Memory(string entityId, AetherClient client, MemoryOptions? options = null)
        : this(entityId, client ?? throw new ArgumentNullException(nameof(client)), ownsClient: false, options)
    {
    }

    private Memory(string entityId, AetherClient client, bool ownsClient, MemoryOptions? options)
    {
        ValidateEntityId(entityId);
        _entityId = entityId;
        _client = client;
        _ownsClient = ownsClient;
        _halfLife = options?.HalfLife is { } hl && hl > TimeSpan.Zero ? hl : DefaultHalfLife;
        _extractFacts = options?.ExtractFacts ?? false;
        _clock = options?.Clock ?? (() => DateTimeOffset.UtcNow);
    }

    private static void ValidateEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("entityId cannot be empty and must contain a non-whitespace character", nameof(entityId));
        if (entityId.Length > 256)
            throw new ArgumentException("entityId cannot be longer than 256 characters", nameof(entityId));
    }

    // ── remember ──────────────────────────────────────────────────────

    /// <summary>
    /// Stores one memory for this entity. One HTTP call.
    /// </summary>
    /// <param name="text">The text to remember. Empty/whitespace-only is an argument error.</param>
    /// <param name="metadata">
    /// Optional structured metadata. String-safe values are also mirrored into
    /// legacy <c>key:value</c> tags where doing so is lossless.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="MemoryItem"/> built from the inserted document.</returns>
    public async Task<MemoryItem> RememberAsync(
        string text,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text cannot be empty", nameof(text));

        // When fact extraction is enabled, the text is distilled server-side
        // into atomic facts (sibling kind:fact memories); the returned item is
        // still the raw memory.
        var tags = EncodeMetadata(metadata);
        var record = await _client.InsertTextAsync(
            text,
            tags: tags,
            entityId: _entityId,
            metadata: metadata,
            extractFacts: _extractFacts,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new MemoryItem
        {
            Id = record.DocId,
            Text = text,
            CreatedAt = record.CreatedAt,
            EntityId = _entityId,
            Metadata = record.Metadata.Count > 0
                ? record.Metadata
                : (metadata?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>()),
            Score = null,
        };
    }

    public Task<MemoryItem> RememberAsync(
        string text,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?>? typed = metadata?
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        return RememberAsync(text, typed, cancellationToken);
    }

    private static IReadOnlyList<string>? EncodeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        // Sort by KEY (ordinal) BEFORE assembling, so the wire string is
        // byte-identical across languages (Python sorted(), Go sort.Strings,
        // TS .sort()). Sorting the assembled "key:value" strings instead would
        // diverge when one key is a prefix of another and the longer key's next
        // char sorts below ':' (0x3A) — e.g. {a:v, a0:w} → "a0:w,a:v" not "a:v,a0:w".
        var keys = new List<string>(metadata.Keys);
        keys.Sort(StringComparer.Ordinal);

        var tags = new List<string>(metadata.Count);
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException(
                    "metadata key cannot be empty", nameof(metadata));
            var value = metadata[key];
            var stringValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (key.Contains(":") || key.Contains(",") || stringValue.Contains(","))
                throw new ArgumentException(
                    $"metadata key/value for '{key}' cannot contain ':' in keys or ',' in keys/values", nameof(metadata));
            tags.Add($"{key}:{stringValue}");
        }

        return tags;
    }

    // ── recall ────────────────────────────────────────────────────────

    /// <summary>
    /// Semantic search scoped to this entity, with optional client-side recency
    /// decay.
    /// </summary>
    /// <param name="query">Natural-language query.</param>
    /// <param name="k">Maximum results. Default 5.</param>
    /// <param name="recencyWeight">
    /// Blend weight in <c>[0, 1]</c> (clamped). 0 (default) = pure relevance, one
    /// cheap <c>retrieve</c> call with null timestamps. Greater than 0 enables the
    /// recency-decay re-rank, which costs N additional <c>get</c> calls to resolve
    /// timestamps (parallelized).
    /// </param>
    /// <param name="since">Optional RFC 3339 lower bound (inclusive), forwarded verbatim.</param>
    /// <param name="until">Optional RFC 3339 upper bound (inclusive), forwarded verbatim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<MemoryItem>> RecallAsync(
        string query,
        int k = 5,
        double recencyWeight = 0.0,
        string? since = null,
        string? until = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query cannot be empty or whitespace-only", nameof(query));
        if (k < 1)
            throw new ArgumentOutOfRangeException(nameof(k), "k must be at least 1");

        var w = recencyWeight < 0.0 ? 0.0 : recencyWeight > 1.0 ? 1.0 : recencyWeight;

        // Mode A — pure relevance (1 call).
        if (w == 0.0)
        {
            var hits = await _client.RetrieveAsync(
                query, k: k, entityId: _entityId, since: since, until: until,
                filter: filter,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var items = new List<MemoryItem>(hits.Count);
            foreach (var h in hits)
            {
                items.Add(new MemoryItem
                {
                    Id = h.DocId,
                    Text = h.Content,
                    CreatedAt = null,
                    EntityId = _entityId,
                    Metadata = h.Metadata,
                    Score = Similarity(h.Score),
                });
            }
            return items;
        }

        // Mode B — recency decay (N+1 calls).
        var overfetchK = Math.Min(k * Overfetch, MaxCandidates);
        var candidates = await _client.RetrieveAsync(
            query, k: overfetchK, entityId: _entityId, since: since, until: until,
            filter: filter,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
            return Array.Empty<MemoryItem>();

        // Resolve created_at per unique doc_id (parallelized).
        var uniqueIds = new List<string>();
        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            if (seen.Add(c.DocId))
                uniqueIds.Add(c.DocId);
        }

        var getTasks = uniqueIds
            .Select(id => _client.GetAsync(id, cancellationToken))
            .ToArray();
        var records = await Task.WhenAll(getTasks).ConfigureAwait(false);

        var createdById = new Dictionary<string, string?>(uniqueIds.Count);
        for (int i = 0; i < uniqueIds.Count; i++)
            createdById[uniqueIds[i]] = records[i].CreatedAt;

        var now = _clock();

        var scored = new List<ScoredCandidate>(candidates.Count);
        foreach (var c in candidates)
        {
            var similarity = Similarity(c.Score);
            createdById.TryGetValue(c.DocId, out var created);
            var recency = RecencyScore(created, now, _halfLife);
            var blended = (1.0 - w) * similarity + w * recency;
            scored.Add(new ScoredCandidate(c, created, blended));
        }

        // Total order → deterministic: blended DESC, score DESC, doc_id ASC.
        scored.Sort((a, b) =>
        {
            int cmp = b.Blended.CompareTo(a.Blended);
            if (cmp != 0) return cmp;
            cmp = b.Candidate.Score.CompareTo(a.Candidate.Score);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Candidate.DocId, b.Candidate.DocId);
        });

        var result = new List<MemoryItem>(Math.Min(k, scored.Count));
        for (int i = 0; i < scored.Count && i < k; i++)
        {
            var s = scored[i];
            result.Add(new MemoryItem
            {
                Id = s.Candidate.DocId,
                Text = s.Candidate.Content,
                CreatedAt = s.CreatedAt,
                EntityId = _entityId,
                Metadata = s.Candidate.Metadata,
                Score = s.Blended,
            });
        }

        return result;
    }

    private readonly struct ScoredCandidate
    {
        public ScoredCandidate(RetrievalResult candidate, string? createdAt, double blended)
        {
            Candidate = candidate;
            CreatedAt = createdAt;
            Blended = blended;
        }

        public RetrievalResult Candidate { get; }
        public string? CreatedAt { get; }
        public double Blended { get; }
    }

    /// <summary>
    /// Normalizes a calibrated 0–100 relevance <paramref name="score"/> (higher =
    /// better) to <c>[0, 1]</c> so it shares the recency term's scale and the
    /// Mode B blend stays well-defined.
    /// </summary>
    private static double Similarity(int score) => score / 100.0;

    private static double RecencyScore(string? created, DateTimeOffset now, TimeSpan halfLife)
    {
        if (string.IsNullOrEmpty(created))
            return 0.0;
        if (!TryParseRfc3339(created!, out var createdAt))
            return 0.0;

        var ageDays = (now - createdAt).TotalDays;
        if (ageDays < 0.0)
            ageDays = 0.0; // future timestamps → age 0 → score 1.0

        var halfLifeDays = halfLife.TotalDays;
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    private static bool TryParseRfc3339(string value, out DateTimeOffset result)
    {
        // DateTimeOffset.Parse with AssumeUniversal treats a naive (no-offset)
        // timestamp as UTC; 'Z' and explicit offsets are handled natively.
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out result);
    }

    // ── list ──────────────────────────────────────────────────────────

    /// <summary>
    /// Chronological view of this entity's memories, newest first.
    /// </summary>
    /// <param name="since">Optional RFC 3339 lower bound (inclusive), forwarded verbatim.</param>
    /// <param name="until">Optional RFC 3339 upper bound (inclusive), forwarded verbatim.</param>
    /// <param name="limit">Maximum items. Default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Costs <b>1 + N</b> calls: one listing plus one content download per item
    /// (the listing endpoint returns metadata, not text). Callers who need only
    /// metadata can drop to the raw <c>client.ListAsync(entityId: ...)</c>.
    /// </remarks>
    public async Task<IReadOnlyList<MemoryItem>> ListAsync(
        string? since = null,
        string? until = null,
        IReadOnlyDictionary<string, object?>? filter = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var listing = await _client.ListAsync(
            limit: limit, entityId: _entityId, since: since, until: until,
            filter: filter,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var records = listing.Documents;
        if (records.Count > limit)
            records = records.GetRange(0, limit);

        // Download text per record (parallelized), preserving newest-first order.
        var downloadTasks = records
            .Select(r => _client.DownloadAsync(r.DocId, cancellationToken))
            .ToArray();
        var payloads = await Task.WhenAll(downloadTasks).ConfigureAwait(false);

        var items = new List<MemoryItem>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            items.Add(new MemoryItem
            {
                Id = r.DocId,
                Text = Encoding.UTF8.GetString(payloads[i]),
                CreatedAt = r.CreatedAt,
                EntityId = r.EntityId ?? _entityId,
                Metadata = r.Metadata,
                Score = null,
            });
        }

        return items;
    }

    /// <summary>
    /// Returns this entity's consolidated <b>extracted</b> facts (<c>kind:fact</c>
    /// memories), highest corroborated confidence first.
    /// </summary>
    /// <remarks>
    /// These are the free-text facts produced by <see cref="RememberAsync"/> with
    /// fact extraction enabled and deduped server-side — distinct from the
    /// structured memory-graph facts returned by <see cref="ListFactsAsync"/>.
    /// One entry per distinct fact, most-corroborated (then most-recent) first.
    /// Cost is 1 + N (one listing plus a content download per fact).
    /// </remarks>
    /// <param name="limit">Maximum number of facts to return. Default 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<MemoryItem>> ListExtractedFactsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var listing = await _client.ListAsync(
            limit: limit, entityId: _entityId, tags: new[] { "kind:fact" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var records = listing.Documents
            .OrderByDescending(r => FactConfidence(r.Tags))
            .ThenByDescending(r => r.CreatedAt ?? string.Empty)
            .Take(limit)
            .ToList();

        var downloadTasks = records
            .Select(r => _client.DownloadAsync(r.DocId, cancellationToken))
            .ToArray();
        var payloads = await Task.WhenAll(downloadTasks).ConfigureAwait(false);

        var items = new List<MemoryItem>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            items.Add(new MemoryItem
            {
                Id = r.DocId,
                Text = Encoding.UTF8.GetString(payloads[i]),
                CreatedAt = r.CreatedAt,
                EntityId = r.EntityId ?? _entityId,
                Metadata = r.Metadata,
                Score = null,
            });
        }

        return items;
    }

    /// <summary>Confidence (corroborating-source count) from a fact's <c>conf:</c> tag; 1 default.</summary>
    private static int FactConfidence(IReadOnlyList<string> tags)
    {
        foreach (var t in tags)
        {
            if (t.StartsWith("conf:", StringComparison.Ordinal))
            {
                return int.TryParse(t.Substring("conf:".Length), out var n) && n > 0 ? n : 1;
            }
        }
        return 1;
    }

    // ── forget ────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes one memory (soft tombstone; restorable via the raw client's
    /// <c>RestoreAsync</c>, which <see cref="Memory"/> does not expose in v1).
    /// </summary>
    /// <param name="id">The memory ID. Empty is an argument error.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ForgetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id cannot be empty", nameof(id));
        await _client.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every memory for this entity and returns the count deleted. Pages the
    /// listing in batches of 1000 and deletes each ID until the listing is exhausted
    /// (tombstones drop out of subsequent listings, so the loop terminates).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of memories deleted.</returns>
    public async Task<int> ForgetAllAsync(CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        while (true)
        {
            var listing = await _client.ListAsync(
                limit: 1000, entityId: _entityId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (listing.Documents.Count == 0)
                break;

            foreach (var record in listing.Documents)
            {
                await _client.DeleteAsync(record.DocId, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
        }

        return deleted;
    }

    // ── Memory graph ──────────────────────────────────────────────────
    //
    // Typed entities, directed relationships, temporal facts, and consolidation
    // over the engine's /v1/memory/* routes. These are NOT sugar over insert/retrieve
    // — they reach new routes through the internal transport hook
    // (AetherClient.SendMemoryAsync), reusing the raw client's URL building,
    // partition scoping, retries, and error mapping. The public raw-client surface
    // is unchanged. entity_id (the owner) is on the query of every call; partition
    // is injected from the client scope by the hook. Optional filters are sent only
    // when provided; POST bodies omit unset optional fields (a Dictionary with a
    // null value still serializes the key as JSON null — used for fact `value`).

    private static readonly string[] ValidSubjectTypes = { "owner", "entity", "relationship" };

    private static void RequireNonEmpty(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} cannot be empty", name);
    }

    // Validates (subjectType, subjectId) client-side. Returns the
    // effective subjectId: null for owner (ignored), the selector otherwise.
    private static string? ValidateSubject(string subjectType, string? subjectId)
    {
        if (Array.IndexOf(ValidSubjectTypes, subjectType) < 0)
            throw new ArgumentException(
                "subjectType must be 'owner', 'entity', or 'relationship'", nameof(subjectType));
        if (subjectType == "owner")
            return null;
        if (string.IsNullOrEmpty(subjectId))
            throw new ArgumentException(
                $"subjectId is required when subjectType is '{subjectType}'", nameof(subjectId));
        return subjectId;
    }

    private static void ValidateCardinality(string? cardinality)
    {
        if (cardinality != null && cardinality != "single" && cardinality != "multi")
            throw new ArgumentException("cardinality must be 'single' or 'multi'", nameof(cardinality));
    }

    // Builds the base query string with entity_id=<owner> plus any provided
    // filter pairs (already-escaped key=value strings). Partition is appended by
    // the transport hook, not here.
    private string GraphQuery(params string[] filters)
    {
        var sb = new StringBuilder();
        sb.Append("entity_id=").Append(Uri.EscapeDataString(_entityId));
        foreach (var f in filters)
        {
            if (!string.IsNullOrEmpty(f))
                sb.Append('&').Append(f);
        }
        return sb.ToString();
    }

    private static string Pair(string key, string value) =>
        $"{key}={Uri.EscapeDataString(value)}";

    // ── Entities ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates a typed entity node in this owner's graph
    /// (<c>POST /v1/memory/entities</c>). Omit <paramref name="memoryEntityId"/> to mint
    /// a new node; pass an existing one (or an idempotency key) to update it.
    /// </summary>
    /// <param name="entityType">Caller-controlled type. Non-empty/non-whitespace.</param>
    /// <param name="memoryEntityId">Optional node id / idempotency key.</param>
    /// <param name="displayName">Optional label.</param>
    /// <param name="aliases">Optional aliases.</param>
    /// <param name="attributes">Optional scalar attribute map.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="entityType"/> is empty or whitespace.</exception>
    public Task<MemoryEntity> UpsertEntityAsync(
        string entityType,
        string? memoryEntityId = null,
        string? displayName = null,
        IReadOnlyList<string>? aliases = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken ct = default)
    {
        RequireNonEmpty(nameof(entityType), entityType);
        var body = new Dictionary<string, object?> { ["entity_type"] = entityType };
        if (!string.IsNullOrEmpty(memoryEntityId))
            body["memory_entity_id"] = memoryEntityId;
        if (displayName != null)
            body["display_name"] = displayName;
        if (aliases != null)
            body["aliases"] = aliases;
        if (attributes != null)
            body["attributes"] = attributes;

        return _client.SendMemoryAsync<MemoryEntity>(
            HttpMethod.Post, "/memory/entities", GraphQuery(), body, ct);
    }

    /// <summary>Fetches one entity node by id (<c>GET /v1/memory/entities/{id}</c>).</summary>
    /// <param name="memoryEntityId">The node id. Non-empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="memoryEntityId"/> is empty or whitespace.</exception>
    public Task<MemoryEntity> GetEntityAsync(
        string memoryEntityId,
        CancellationToken ct = default)
    {
        RequireNonEmpty(nameof(memoryEntityId), memoryEntityId);
        var path = $"/memory/entities/{Uri.EscapeDataString(memoryEntityId)}";
        return _client.SendMemoryAsync<MemoryEntity>(
            HttpMethod.Get, path, GraphQuery(), null, ct);
    }

    /// <summary>
    /// Lists this owner's entity nodes (<c>GET /v1/memory/entities</c>), optionally
    /// filtered by <paramref name="entityType"/>. Unset filters are absent from the query.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(
        string? entityType = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var query = GraphQuery(
            string.IsNullOrEmpty(entityType) ? null! : Pair("entity_type", entityType!),
            limit.HasValue ? Pair("limit", limit.Value.ToString(CultureInfo.InvariantCulture)) : null!);
        var resp = await _client.SendMemoryAsync<MemoryEntityListResponse>(
            HttpMethod.Get, "/memory/entities", query, null, ct).ConfigureAwait(false);
        return resp.Entities;
    }

    // ── Relationships ─────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates a directed edge between two entity nodes
    /// (<c>POST /v1/memory/relationships</c>).
    /// </summary>
    /// <param name="fromEntityId">Source node id. Non-empty.</param>
    /// <param name="toEntityId">Target node id. Non-empty.</param>
    /// <param name="relationshipType">Edge type. Non-empty.</param>
    /// <param name="relationshipId">Optional edge id / idempotency key.</param>
    /// <param name="attributes">Optional scalar attribute map.</param>
    /// <param name="validFrom">Optional RFC 3339 effective time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Any of the three required ids is empty or whitespace.</exception>
    public Task<MemoryRelationship> RelateAsync(
        string fromEntityId,
        string toEntityId,
        string relationshipType,
        string? relationshipId = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string? validFrom = null,
        CancellationToken ct = default)
    {
        RequireNonEmpty(nameof(fromEntityId), fromEntityId);
        RequireNonEmpty(nameof(toEntityId), toEntityId);
        RequireNonEmpty(nameof(relationshipType), relationshipType);
        var body = new Dictionary<string, object?>
        {
            ["from_entity_id"] = fromEntityId,
            ["to_entity_id"] = toEntityId,
            ["relationship_type"] = relationshipType,
        };
        if (!string.IsNullOrEmpty(relationshipId))
            body["relationship_id"] = relationshipId;
        if (attributes != null)
            body["attributes"] = attributes;
        if (validFrom != null)
            body["valid_from"] = validFrom;

        return _client.SendMemoryAsync<MemoryRelationship>(
            HttpMethod.Post, "/memory/relationships", GraphQuery(), body, ct);
    }

    /// <summary>
    /// Lists edges (<c>GET /v1/memory/relationships</c>), optionally filtered.
    /// <paramref name="includeInactive"/> is sent (<c>=true</c>) only when true;
    /// <paramref name="asOf"/> returns edges active at that instant.
    /// </summary>
    public async Task<IReadOnlyList<MemoryRelationship>> ListRelationshipsAsync(
        string? fromEntityId = null,
        string? toEntityId = null,
        string? relationshipType = null,
        bool includeInactive = false,
        string? asOf = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var query = GraphQuery(
            string.IsNullOrEmpty(fromEntityId) ? null! : Pair("from_entity_id", fromEntityId!),
            string.IsNullOrEmpty(toEntityId) ? null! : Pair("to_entity_id", toEntityId!),
            string.IsNullOrEmpty(relationshipType) ? null! : Pair("relationship_type", relationshipType!),
            includeInactive ? "include_inactive=true" : null!,
            string.IsNullOrEmpty(asOf) ? null! : Pair("as_of", asOf!),
            limit.HasValue ? Pair("limit", limit.Value.ToString(CultureInfo.InvariantCulture)) : null!);
        var resp = await _client.SendMemoryAsync<MemoryRelationshipListResponse>(
            HttpMethod.Get, "/memory/relationships", query, null, ct).ConfigureAwait(false);
        return resp.Relationships;
    }

    // ── Facts (temporal, with contradiction resolution) ───────────────

    /// <summary>
    /// Asserts a temporal fact about the owner (default), an entity, or a
    /// relationship (<c>POST /v1/memory/facts</c>). A newer single-valued fact with the
    /// same (subject, predicate) supersedes the prior one server-side, keeping it in
    /// history.
    /// </summary>
    /// <param name="predicate">Caller-controlled predicate. Non-empty.</param>
    /// <param name="value">Scalar value (string | number | bool | null). Always sent, even when null.</param>
    /// <param name="subjectType"><c>owner</c> (default), <c>entity</c>, or <c>relationship</c>.</param>
    /// <param name="subjectId">Required when <paramref name="subjectType"/> is entity/relationship; ignored for owner.</param>
    /// <param name="cardinality">Optional <c>single</c> or <c>multi</c>.</param>
    /// <param name="validFrom">Optional RFC 3339 effective time.</param>
    /// <param name="observedAt">Optional RFC 3339 ingest time.</param>
    /// <param name="supersedesFactId">Optional prior fact this one replaces.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Invalid predicate, subject type, missing subject id, or invalid cardinality.</exception>
    public Task<MemoryFact> RememberFactAsync(
        string predicate,
        object? value,
        string subjectType = "owner",
        string? subjectId = null,
        string? cardinality = null,
        string? validFrom = null,
        string? observedAt = null,
        string? supersedesFactId = null,
        CancellationToken ct = default)
    {
        RequireNonEmpty(nameof(predicate), predicate);
        var effectiveSubjectId = ValidateSubject(subjectType, subjectId);
        ValidateCardinality(cardinality);

        // `value` is ALWAYS in the body, even when null (a null Dictionary value
        // serializes as an explicit JSON null, which is what the engine expects).
        var body = new Dictionary<string, object?>
        {
            ["subject_type"] = subjectType,
            ["predicate"] = predicate,
            ["value"] = value,
        };
        if (effectiveSubjectId != null)
            body["subject_id"] = effectiveSubjectId;
        if (cardinality != null)
            body["cardinality"] = cardinality;
        if (validFrom != null)
            body["valid_from"] = validFrom;
        if (observedAt != null)
            body["observed_at"] = observedAt;
        if (!string.IsNullOrEmpty(supersedesFactId))
            body["supersedes_fact_id"] = supersedesFactId;

        return _client.SendMemoryAsync<MemoryFact>(
            HttpMethod.Post, "/memory/facts", GraphQuery(), body, ct);
    }

    /// <summary>
    /// Lists active facts (<c>GET /v1/memory/facts</c>), or include superseded/retracted
    /// with <paramref name="includeInactive"/>. When <paramref name="subjectType"/> is
    /// provided it must be valid, and <paramref name="subjectId"/> is required for
    /// entity/relationship (it is the selector).
    /// </summary>
    /// <exception cref="ArgumentException">Invalid subject type or a non-owner subject without an id.</exception>
    public async Task<IReadOnlyList<MemoryFact>> ListFactsAsync(
        string? subjectType = null,
        string? subjectId = null,
        string? predicate = null,
        bool includeInactive = false,
        string? asOf = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        string? subjectTypeParam = null;
        string? subjectIdParam = null;
        if (subjectType != null)
        {
            var effectiveSubjectId = ValidateSubject(subjectType, subjectId);
            subjectTypeParam = subjectType;
            subjectIdParam = effectiveSubjectId;
        }

        var query = GraphQuery(
            subjectTypeParam == null ? null! : Pair("subject_type", subjectTypeParam),
            subjectIdParam == null ? null! : Pair("subject_id", subjectIdParam),
            string.IsNullOrEmpty(predicate) ? null! : Pair("predicate", predicate!),
            includeInactive ? "include_inactive=true" : null!,
            string.IsNullOrEmpty(asOf) ? null! : Pair("as_of", asOf!),
            limit.HasValue ? Pair("limit", limit.Value.ToString(CultureInfo.InvariantCulture)) : null!);
        var resp = await _client.SendMemoryAsync<MemoryFactListResponse>(
            HttpMethod.Get, "/memory/facts", query, null, ct).ConfigureAwait(false);
        return resp.Facts;
    }

    /// <summary>
    /// Full assertion chain (active + superseded) for one (subject, predicate)
    /// (<c>GET /v1/memory/facts?history=true</c>). Always sends <c>history=true</c> plus
    /// the required <c>subject_type</c> and <c>predicate</c> (and <c>subject_id</c>
    /// for a non-owner subject).
    /// </summary>
    /// <exception cref="ArgumentException">Invalid predicate, subject type, or missing subject id.</exception>
    public async Task<IReadOnlyList<MemoryFact>> FactHistoryAsync(
        string predicate,
        string subjectType = "owner",
        string? subjectId = null,
        CancellationToken ct = default)
    {
        RequireNonEmpty(nameof(predicate), predicate);
        var effectiveSubjectId = ValidateSubject(subjectType, subjectId);

        var query = GraphQuery(
            "history=true",
            Pair("subject_type", subjectType),
            Pair("predicate", predicate),
            effectiveSubjectId == null ? null! : Pair("subject_id", effectiveSubjectId));
        var resp = await _client.SendMemoryAsync<MemoryFactListResponse>(
            HttpMethod.Get, "/memory/facts", query, null, ct).ConfigureAwait(false);
        return resp.Facts;
    }

    // ── Consolidation ─────────────────────────────────────────────────

    /// <summary>
    /// Soft-retracts redundant facts in this scope (<c>POST /v1/memory/consolidate</c>);
    /// returns a report. Sends no body.
    /// </summary>
    public Task<ConsolidationReport> ConsolidateAsync(CancellationToken ct = default)
    {
        return _client.SendMemoryAsync<ConsolidationReport>(
            HttpMethod.Post, "/memory/consolidate", GraphQuery(), null, ct);
    }

    // ── IDisposable ────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the owned raw client when this <see cref="Memory"/> built it. A
    /// client supplied through the dependency-injection constructor is left for the
    /// caller to dispose.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsClient)
                _client.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
