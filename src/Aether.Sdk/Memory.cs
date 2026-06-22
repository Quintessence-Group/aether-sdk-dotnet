using System.Globalization;
using System.Text;

namespace Aether.Sdk;

/// <summary>
/// A single remembered item, returned by <see cref="Memory"/>'s
/// <see cref="Memory.RememberAsync"/>, <see cref="Memory.RecallAsync"/>, and
/// <see cref="Memory.ListAsync"/>.
/// </summary>
/// <remarks>
/// There is no <c>Metadata</c> field. The raw document API does not echo
/// <c>tags</c> on any read model, so metadata written by
/// <see cref="Memory.RememberAsync"/> cannot be read back in v1 (see the README's
/// metadata write-only note). A <c>Metadata</c> field can be added without a
/// breaking change once the server starts echoing tags.
/// </remarks>
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
    /// Reserved no-op flag (default false). When a server-side fact-extraction
    /// endpoint exists, enabling this will let <c>remember</c> split one call into
    /// multiple fact-memories. In v1 it has no effect.
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
/// Metadata passed to <see cref="RememberAsync"/> is written as searchable
/// <c>key:value</c> tags but cannot be read back in v1 — the raw read models do not
/// echo tags. See the README's metadata write-only note.
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
    /// Optional metadata, written as <c>key:value</c> tags (one tag per pair, split
    /// on the first <c>:</c>). <b>Write-only in v1</b>: it cannot be read back via
    /// <see cref="MemoryItem"/>. A value containing a comma is an argument error
    /// (the tag wire format is comma-joined and cannot escape commas).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="MemoryItem"/> built from the inserted document.</returns>
    public async Task<MemoryItem> RememberAsync(
        string text,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text cannot be empty", nameof(text));

        // extract_facts is a reserved no-op in v1; remember stores the text as a
        // single memory regardless of the flag (see the README).
        _ = _extractFacts;

        var tags = EncodeMetadata(metadata);
        var record = await _client.InsertTextAsync(
            text,
            tags: tags,
            entityId: _entityId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new MemoryItem
        {
            Id = record.DocId,
            Text = text,
            CreatedAt = record.CreatedAt,
            EntityId = _entityId,
            Score = null,
        };
    }

    private static IReadOnlyList<string>? EncodeMetadata(IReadOnlyDictionary<string, string>? metadata)
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
            // The first ':' separates key from value, so a key containing ':'
            // would be ambiguous; ',' is the tag-list separator and cannot escape.
            if (key.Contains(":"))
                throw new ArgumentException(
                    $"metadata key '{key}' cannot contain a colon", nameof(metadata));
            if (key.Contains(","))
                throw new ArgumentException(
                    $"metadata key '{key}' cannot contain a comma", nameof(metadata));
            var value = metadata[key];
            if (value != null && value.Contains(","))
                throw new ArgumentException(
                    $"metadata value for '{key}' cannot contain a comma", nameof(metadata));
            // First ':' separates key from value; values may contain ':'.
            tags.Add($"{key}:{value}");
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
                    Score = Similarity(h.Score),
                });
            }
            return items;
        }

        // Mode B — recency decay (N+1 calls).
        var overfetchK = Math.Min(k * Overfetch, MaxCandidates);
        var candidates = await _client.RetrieveAsync(
            query, k: overfetchK, entityId: _entityId, since: since, until: until,
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
    /// better, since the 0.3.0 search redesign) to <c>[0, 1]</c> so it shares the
    /// recency term's scale and the Mode B blend stays well-defined.
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
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var listing = await _client.ListAsync(
            limit: limit, entityId: _entityId, since: since, until: until,
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
                Score = null,
            });
        }

        return items;
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
