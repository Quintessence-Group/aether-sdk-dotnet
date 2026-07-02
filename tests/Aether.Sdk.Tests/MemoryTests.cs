using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

/// <summary>
/// Contract tests for <see cref="Memory"/> covering the cross-SDK memory contract.
///
/// These mock the SAME transport layer as <see cref="AetherClientTests"/> (a stub
/// <see cref="HttpMessageHandler"/>); the real <see cref="AetherClient"/> runs and
/// <see cref="Memory"/> wraps it via the dependency-injection constructor. Nothing in
/// <see cref="Memory"/> itself is mocked.
/// </summary>
public class MemoryTests
{
    // A recording handler that routes by path+method so a single Memory call (which
    // may issue several HTTP requests) can be served deterministically.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _route;

        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> route)
        {
            _route = route;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!, body));
            }
            return _route(request, body);
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Bytes(string text) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static Memory CreateMemory(string entityId, RoutingHandler handler, MemoryOptions? options = null)
    {
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");
        return new Memory(entityId, client, options);
    }

    private static string PathOf(Uri uri) => uri.AbsolutePath;

    // ── Case 1 + 2 + 3: remember scoping / round-trip / metadata→tags ──────

    [Fact]
    public async Task Remember_SendsEntityIdAndReturnsItem()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            doc_id = "mem-1",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 5,
            chunks = 1,
            vectors = 1,
            version = 1,
            entity_id = "patient-john",
            created_at = "2026-06-15T12:00:00Z",
        }));

        var mem = CreateMemory("patient-john", handler);
        var item = await mem.RememberAsync("Anxious about flying");

        // §8.1 scoping: entity_id sent as a first-class query field on insert.
        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/documents", PathOf(req.Uri));
        Assert.Contains("entity_id=patient-john", req.Uri.Query);

        // §8.2 round-trip: id + created_at from the response.
        Assert.Equal("mem-1", item.Id);
        Assert.Equal("2026-06-15T12:00:00Z", item.CreatedAt);
        Assert.Equal("Anxious about flying", item.Text);
        Assert.Equal("patient-john", item.EntityId);
        Assert.Null(item.Score);
    }

    [Fact]
    public async Task Remember_EncodesMetadataAsTags()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            doc_id = "mem-2",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 3,
            chunks = 1,
            vectors = 1,
            version = 1,
        }));

        var mem = CreateMemory("user-7", handler);
        await mem.RememberAsync("hi", new Dictionary<string, string>
        {
            ["topic"] = "anxiety",
        });

        // §8.3 metadata → tags: topic:anxiety appears as a tag query param.
        var req = handler.Requests.Single();
        var query = Uri.UnescapeDataString(req.Uri.Query);
        Assert.Contains("tags=topic:anxiety", query);
    }

    [Fact]
    public async Task Remember_MetadataValueMayContainColon()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            doc_id = "mem-3",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 3,
            chunks = 1,
            vectors = 1,
            version = 1,
        }));

        var mem = CreateMemory("user-7", handler);
        await mem.RememberAsync("hi", new Dictionary<string, string>
        {
            ["url"] = "https://example.com",
        });

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        // First ':' separates key from value; the value keeps its remaining colons.
        Assert.Contains("tags=url:https://example.com", query);
    }

    // ── ExtractFacts constructor default + per-call override ───────────────

    private static HttpResponseMessage InsertJson() => Json(new
    {
        doc_id = "mem-4",
        cid = "hash",
        content_type = "text/plain",
        size_bytes = 3,
        chunks = 1,
        vectors = 1,
        version = 1,
    });

    [Fact]
    public async Task Remember_ConstructorExtractFactsSetsDefault()
    {
        var handler = new RoutingHandler((_, __) => InsertJson());
        var mem = CreateMemory("user-7", handler, new MemoryOptions { ExtractFacts = true });

        await mem.RememberAsync("fact one. fact two.");

        // One insert call carrying extract_facts=true — the fact fan-out is server-side.
        var req = handler.Requests.Single();
        Assert.Contains("extract_facts=true", req.Uri.Query);
    }

    [Fact]
    public async Task Remember_PerCallExtractFalseOverridesConstructorTrue()
    {
        var handler = new RoutingHandler((_, __) => InsertJson());
        var mem = CreateMemory("user-7", handler, new MemoryOptions { ExtractFacts = true });

        await mem.RememberAsync("fact one. fact two.", extract: false);

        Assert.DoesNotContain("extract_facts", handler.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Remember_PerCallExtractTrueOverridesDefaultOff()
    {
        var handler = new RoutingHandler((_, __) => InsertJson());
        var mem = CreateMemory("user-7", handler);

        await mem.RememberAsync("fact one. fact two.", extract: true);

        Assert.Contains("extract_facts=true", handler.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Remember_DefaultOffSendsNoExtractFlag()
    {
        var handler = new RoutingHandler((_, __) => InsertJson());
        var mem = CreateMemory("user-7", handler);

        await mem.RememberAsync("plain memory");

        Assert.DoesNotContain("extract_facts", handler.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Remember_RejectsCommaInMetadataValue_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("user-7", handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberAsync("hi", new Dictionary<string, string> { ["a"] = "b,c" }));

        // §8.3: no HTTP call is made when the value is rejected client-side.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remember_RejectsEmptyText_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("user-7", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.RememberAsync("   "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remember_RejectsEmptyMetadataKey_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("user-7", handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberAsync("hi", new Dictionary<string, string> { [""] = "v" }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remember_RejectsColonInMetadataKey_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("user-7", handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberAsync("hi", new Dictionary<string, string> { ["a:b"] = "v" }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remember_RejectsCommaInMetadataKey_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("user-7", handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberAsync("hi", new Dictionary<string, string> { ["a,b"] = "v" }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Remember_EmitsTagsSortedByKey()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            doc_id = "mem-sorted",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 3,
            chunks = 1,
            vectors = 1,
            version = 1,
        }));

        var mem = CreateMemory("user-7", handler);
        // Insertion order is intentionally not alphabetical.
        await mem.RememberAsync("hi", new Dictionary<string, string>
        {
            ["zeta"] = "1",
            ["alpha"] = "2",
            ["mid"] = "3",
        });

        // Tags must be emitted sorted by key so the wire string is byte-identical
        // across languages: alpha:2, mid:3, zeta:1.
        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        Assert.Contains("tags=alpha:2,mid:3,zeta:1", query);
    }

    [Fact]
    public async Task Remember_PrefixKeys_SortedByKeyNotByTag()
    {
        // Regression: sort KEYS, not the assembled "key:value" strings. With a
        // prefix key, key-sort gives a:v,a0:w; a tag-string sort would give
        // a0:w,a:v ('0' 0x30 < ':' 0x3A). Must match py/ts/go byte-for-byte.
        var handler = new RoutingHandler((_, __) => Json(new
        {
            doc_id = "mem-prefix",
            cid = "hash",
            content_type = "text/plain",
            size_bytes = 3,
            chunks = 1,
            vectors = 1,
            version = 1,
        }));

        var mem = CreateMemory("user-7", handler);
        await mem.RememberAsync("hi", new Dictionary<string, string>
        {
            ["a0"] = "w",
            ["a"] = "v",
        });

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        Assert.Contains("tags=a:v,a0:w", query);
    }

    // ── Case 4: recall default (recencyWeight = 0) ─────────────────────────

    [Fact]
    public async Task Recall_Default_OneCall_NullCreatedAt_ServerOrder()
    {
        var handler = new RoutingHandler((m, _) =>
        {
            // Only the search/retrieve endpoint should be hit.
            Assert.Equal("/v1/search", PathOf(m.RequestUri!));
            return Json(new
            {
                query = "anxiety",
                results = new[]
                {
                    new { doc_id = "r1", score = 90, content = "first", content_type = "text/plain" },
                    new { doc_id = "r2", score = 80, content = "second", content_type = "text/plain" },
                },
            });
        });

        var mem = CreateMemory("patient-john", handler);
        var hits = await mem.RecallAsync("anxiety", k: 5);

        // §8.4: exactly one retrieve call.
        var req = handler.Requests.Single();
        Assert.Contains("include_content=true", req.Uri.Query);
        Assert.Contains("entity_id=patient-john", req.Uri.Query);
        Assert.Contains("k=5", req.Uri.Query);

        // Order = server order; created_at null.
        Assert.Equal(new[] { "r1", "r2" }, hits.Select(h => h.Id).ToArray());
        Assert.Equal(new[] { "first", "second" }, hits.Select(h => h.Text).ToArray());
        Assert.All(hits, h => Assert.Null(h.CreatedAt));
        Assert.All(hits, h => Assert.Equal("patient-john", h.EntityId));
        // Score = wire score / 100 (higher = better).
        Assert.Equal(0.90, hits[0].Score!.Value, 9);
        Assert.Equal(0.80, hits[1].Score!.Value, 9);
    }

    [Fact]
    public async Task Recall_RejectsEmptyQuery_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("e1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.RecallAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => mem.RecallAsync("   "));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Recall_RejectsKBelowOne_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("e1", handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mem.RecallAsync("q", k: 0));
        Assert.Empty(handler.Requests);
    }

    // ── Case 5: recall recency re-rank (golden ordering) ───────────────────

    // Dependency-free fixed clock for deterministic recency tests.
    private static Func<DateTimeOffset> FixedClock(DateTimeOffset now) => () => now;

    [Fact]
    public async Task Recall_Recency_MatchesGoldenOrdering()
    {
        // Deterministic recency golden vector (identical ordering in all four SDKs):
        //   now = 2026-06-15T00:00:00Z, half_life = 30d, w = 0.5
        //   blended = 0.5*(score/100) + 0.5*0.5^(age_days/30)
        //   retrieve returns (server order, descending score):
        //     doc-e 95 null               -> recency 0    -> blended 0.475000
        //     doc-a 90 2026-01-01 (165d)  -> recency ~.022 -> blended 0.461049
        //     doc-b 80 2026-06-14 (1d)    -> recency ~.977 -> blended 0.888580
        //     doc-c 70 2026-06-10 (5d)    -> recency ~.891 -> blended 0.795449
        //     doc-d 60 2026-05-16 (30d=1 half-life) -> recency 0.5 -> blended 0.550000
        //   Expected re-ranked order: doc-b, doc-c, doc-d, doc-e, doc-a.
        var created = new Dictionary<string, string?>
        {
            ["doc-e"] = null,
            ["doc-a"] = "2026-01-01T00:00:00Z",
            ["doc-b"] = "2026-06-14T00:00:00Z",
            ["doc-c"] = "2026-06-10T00:00:00Z",
            ["doc-d"] = "2026-05-16T00:00:00Z",
        };

        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/search")
            {
                return Json(new
                {
                    query = "q",
                    results = new[]
                    {
                        new { doc_id = "doc-e", score = 95, content = "EEE", content_type = "text/plain" },
                        new { doc_id = "doc-a", score = 90, content = "AAA", content_type = "text/plain" },
                        new { doc_id = "doc-b", score = 80, content = "BBB", content_type = "text/plain" },
                        new { doc_id = "doc-c", score = 70, content = "CCC", content_type = "text/plain" },
                        new { doc_id = "doc-d", score = 60, content = "DDD", content_type = "text/plain" },
                    },
                });
            }

            // get(doc_id) — resolve created_at (doc-e has a null timestamp).
            var id = path.Substring("/v1/documents/".Length);
            return Json(new
            {
                doc_id = id,
                cid = "hash",
                content_type = "text/plain",
                size_bytes = 3,
                chunks = 1,
                vectors = 1,
                version = 1,
                created_at = created[id],
            });
        });

        var options = new MemoryOptions
        {
            HalfLife = TimeSpan.FromDays(30),
            Clock = FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
        };
        var mem = CreateMemory("e1", handler, options);

        var hits = await mem.RecallAsync("q", k: 5, recencyWeight: 0.5);

        Assert.Equal(new[] { "doc-b", "doc-c", "doc-d", "doc-e", "doc-a" }, hits.Select(h => h.Id).ToArray());

        // Blended scores carried as Score (assert within 1e-6 absolute tolerance —
        // the pinned values are truncated, so a rounding-to-N-places assertion
        // would spuriously fail; tolerance is the stated criterion).
        Assert.Equal(0.888580, hits[0].Score!.Value, 1e-6);
        Assert.Equal(0.795449, hits[1].Score!.Value, 1e-6);
        Assert.Equal(0.550000, hits[2].Score!.Value, 1e-6);
        Assert.Equal(0.475000, hits[3].Score!.Value, 1e-6);
        Assert.Equal(0.461049, hits[4].Score!.Value, 1e-6);
        Assert.Equal("2026-06-14T00:00:00Z", hits[0].CreatedAt);
        Assert.Null(hits[3].CreatedAt); // doc-e: null timestamp echoed through.

        // N+1: one /search + one /documents/{id} per unique candidate (5).
        Assert.Equal(1, handler.Requests.Count(r => PathOf(r.Uri) == "/v1/search"));
        Assert.Equal(5, handler.Requests.Count(r => PathOf(r.Uri).StartsWith("/v1/documents/")));
    }

    [Fact]
    public async Task Recall_Recency_OverfetchesAndCapsToK()
    {
        // k=2, recencyWeight>0 -> overfetch k*4=8 candidates; only top-2 returned.
        var created = Enumerable.Range(0, 8)
            .ToDictionary(i => $"d{i}", i => $"2026-06-{10 + i:D2}T00:00:00Z");

        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/search")
            {
                // Assert overfetch k applied (k*OVERFETCH = 8).
                Assert.Contains("k=8", m.RequestUri!.Query);
                var results = Enumerable.Range(0, 8).Select(i => new
                {
                    doc_id = $"d{i}",
                    score = 90 - i,
                    content = $"c{i}",
                    content_type = "text/plain",
                }).ToArray();
                return Json(new { query = "q", results });
            }

            var id = path.Substring("/v1/documents/".Length);
            return Json(new
            {
                doc_id = id,
                cid = "h",
                content_type = "text/plain",
                size_bytes = 1,
                chunks = 1,
                vectors = 1,
                version = 1,
                created_at = created[id],
            });
        });

        var options = new MemoryOptions
        {
            Clock = FixedClock(new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
        };
        var mem = CreateMemory("e1", handler, options);

        var hits = await mem.RecallAsync("q", k: 2, recencyWeight: 0.5);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Recall_Recency_EmptyCandidates_NoGetCalls()
    {
        var handler = new RoutingHandler((m, _) =>
        {
            Assert.Equal("/v1/search", PathOf(m.RequestUri!));
            return Json(new { query = "q", results = Array.Empty<object>() });
        });

        var mem = CreateMemory("e1", handler, new MemoryOptions
        {
            Clock = FixedClock(DateTimeOffset.UtcNow),
        });

        var hits = await mem.RecallAsync("q", k: 5, recencyWeight: 0.8);
        Assert.Empty(hits);
        Assert.Single(handler.Requests); // only the search; no get() calls
    }

    [Fact]
    public async Task Recall_Recency_FutureTimestampScoresOne()
    {
        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/search")
            {
                return Json(new
                {
                    query = "q",
                    results = new[]
                    {
                        new { doc_id = "future", score = 5, content = "F", content_type = "text/plain" },
                    },
                });
            }
            return Json(new
            {
                doc_id = "future",
                cid = "h",
                content_type = "text/plain",
                size_bytes = 1,
                chunks = 1,
                vectors = 1,
                version = 1,
                created_at = "2030-01-01T00:00:00Z", // far future relative to fixed now
            });
        });

        var mem = CreateMemory("e1", handler, new MemoryOptions
        {
            HalfLife = TimeSpan.FromDays(30),
            Clock = FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
        });

        var hits = await mem.RecallAsync("q", k: 1, recencyWeight: 1.0);
        // w=1 -> blended == recency; future timestamp clamps age to 0 -> recency 1.0.
        Assert.Single(hits);
        Assert.Equal(1.0, hits[0].Score!.Value, 9);
    }

    [Fact]
    public async Task Recall_Recency_DuplicateDocId_ResolvesOnceWithoutCrashing()
    {
        // Two /search hits share the SAME doc_id. The recency loop must survive this:
        // created_at is resolved "per unique doc_id" (§4), so get() is called exactly
        // ONCE for the shared id — a naive zip of candidates (with the dupe) against the
        // deduped get() results by index would misalign and throw. The implementation
        // keys created_at by doc_id and looks it up per candidate, so both occurrences
        // pick up the same timestamp and the scoring loop completes cleanly.
        var getCalls = 0;
        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/search")
            {
                return Json(new
                {
                    query = "q",
                    results = new[]
                    {
                        new { doc_id = "dup", score = 90, content = "DUP", content_type = "text/plain" },
                        new { doc_id = "dup", score = 70, content = "DUP", content_type = "text/plain" },
                    },
                });
            }

            // get(doc_id) — must be hit only once for the shared "dup" id.
            Interlocked.Increment(ref getCalls);
            var id = path.Substring("/v1/documents/".Length);
            return Json(new
            {
                doc_id = id,
                cid = "hash",
                content_type = "text/plain",
                size_bytes = 3,
                chunks = 1,
                vectors = 1,
                version = 1,
                created_at = "2026-06-14T00:00:00Z",
            });
        });

        var options = new MemoryOptions
        {
            HalfLife = TimeSpan.FromDays(30),
            Clock = FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)),
        };
        var mem = CreateMemory("e1", handler, options);

        var hits = await mem.RecallAsync("q", k: 5, recencyWeight: 0.5);

        // created_at resolved per UNIQUE doc_id: exactly one get() for the shared id
        // (the dedup the recency loop relies on), one /search.
        Assert.Equal(1, getCalls);
        Assert.Equal(1, handler.Requests.Count(r => PathOf(r.Uri) == "/v1/search"));
        Assert.Equal(1, handler.Requests.Count(r => PathOf(r.Uri).StartsWith("/v1/documents/")));

        // The loop did not crash and every emitted item carries the shared id and the
        // resolved timestamp.
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("dup", h.Id));
        Assert.All(hits, h => Assert.Equal("2026-06-14T00:00:00Z", h.CreatedAt));
    }

    // ── Case 6: list (newest-first, downloaded text, entity filter) ────────

    [Fact]
    public async Task List_PopulatesTextViaDownload_SendsEntityFilter()
    {
        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/documents" && m.Method == HttpMethod.Get)
            {
                // Listing is newest-first (server contract).
                return Json(new
                {
                    documents = new[]
                    {
                        new { doc_id = "n1", cid = "", content_type = "text/plain", size_bytes = 4, version = 1, entity_id = "e1", created_at = "2026-06-15T00:00:00Z" },
                        new { doc_id = "n2", cid = "", content_type = "text/plain", size_bytes = 4, version = 1, entity_id = "e1", created_at = "2026-06-14T00:00:00Z" },
                    },
                    count = 2,
                    total = 2,
                    has_more = false,
                });
            }
            // Download endpoint: /v1/documents/{id}/download
            var id = path.Split('/')[3];
            return Bytes($"text-of-{id}");
        });

        var mem = CreateMemory("e1", handler);
        var items = await mem.ListAsync();

        // §8.6 entity filter on the listing request.
        var listReq = handler.Requests.First(r => PathOf(r.Uri) == "/v1/documents");
        Assert.Contains("entity_id=e1", listReq.Uri.Query);

        // Newest-first order preserved; text from per-item download.
        Assert.Equal(new[] { "n1", "n2" }, items.Select(i => i.Id).ToArray());
        Assert.Equal("text-of-n1", items[0].Text);
        Assert.Equal("text-of-n2", items[1].Text);
        Assert.Equal("2026-06-15T00:00:00Z", items[0].CreatedAt);
        Assert.All(items, i => Assert.Equal("e1", i.EntityId));
        Assert.All(items, i => Assert.Null(i.Score));
    }

    // ── Case 7: forget / forget_all ───────────────────────────────────────

    [Fact]
    public async Task Forget_IssuesOneDelete()
    {
        var handler = new RoutingHandler((_, __) => Json(new { status = "tombstoned" }));
        var mem = CreateMemory("e1", handler);

        await mem.ForgetAsync("mem-1");

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("/v1/documents/mem-1", PathOf(req.Uri));
    }

    [Fact]
    public async Task Forget_RejectsEmptyId_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("e1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.ForgetAsync(""));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ForgetAll_DeletesEveryListedId_ReturnsCount()
    {
        // First list page returns 3 docs; tombstones drop out so the second list is empty.
        var listCalls = 0;
        var handler = new RoutingHandler((m, _) =>
        {
            var path = PathOf(m.RequestUri!);
            if (path == "/v1/documents" && m.Method == HttpMethod.Get)
            {
                listCalls++;
                if (listCalls == 1)
                {
                    return Json(new
                    {
                        documents = new[]
                        {
                            new { doc_id = "a", cid = "", content_type = "text/plain", size_bytes = 1, version = 1 },
                            new { doc_id = "b", cid = "", content_type = "text/plain", size_bytes = 1, version = 1 },
                            new { doc_id = "c", cid = "", content_type = "text/plain", size_bytes = 1, version = 1 },
                        },
                        count = 3,
                        total = 3,
                        has_more = false,
                    });
                }
                return Json(new { documents = Array.Empty<object>(), count = 0, total = 0, has_more = false });
            }
            // DELETE
            return Json(new { status = "tombstoned" });
        });

        var mem = CreateMemory("e1", handler);
        var count = await mem.ForgetAllAsync();

        Assert.Equal(3, count);
        var deletes = handler.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal(3, deletes.Count);
        Assert.Contains(deletes, d => PathOf(d.Uri) == "/v1/documents/a");
        Assert.Contains(deletes, d => PathOf(d.Uri) == "/v1/documents/b");
        Assert.Contains(deletes, d => PathOf(d.Uri) == "/v1/documents/c");
        // Listing scoped to the entity, paged at 1000.
        var listReq = handler.Requests.First(r => PathOf(r.Uri) == "/v1/documents");
        Assert.Contains("entity_id=e1", listReq.Uri.Query);
        Assert.Contains("limit=1000", listReq.Uri.Query);
    }

    // ── Case 8: error passthrough (typed billing error, unchanged) ─────────

    [Fact]
    public async Task Recall_SurfacesTypedBillingError_Unchanged()
    {
        var handler = new RoutingHandler((_, __) => Json(
            new { error = "credit exhausted", code = "credit_exhausted" },
            (HttpStatusCode)402));

        var mem = CreateMemory("e1", handler);

        // §8.8: the exact typed error the raw client raises, no Memory wrapping.
        var ex = await Assert.ThrowsAsync<CreditExhaustedException>(() => mem.RecallAsync("q"));
        Assert.Equal((HttpStatusCode)402, ex.StatusCode);
        Assert.Equal("credit_exhausted", ex.ErrorCode);
    }

    [Fact]
    public async Task Remember_SurfacesGenericApiError_Unchanged()
    {
        var handler = new RoutingHandler((_, __) => Json(
            new { error = "Invalid API key" }, HttpStatusCode.Unauthorized));
        var mem = CreateMemory("e1", handler);

        var ex = await Assert.ThrowsAsync<AetherApiException>(() => mem.RememberAsync("hi"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // ── Case 9: invalid construction ───────────────────────────────────────

    [Fact]
    public void Construct_RejectsEmptyEntityId()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        Assert.Throws<ArgumentException>(() => new Memory("", client));
    }

    [Fact]
    public void Construct_RejectsWhitespaceEntityId()
    {
        // A whitespace-only entity_id must be rejected client-side: the raw layer
        // would otherwise drop the blank scope and leak across the whole tenant.
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        Assert.Throws<ArgumentException>(() => new Memory("   ", client));
    }

    [Fact]
    public void Construct_RejectsOversizedEntityId()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        var tooLong = new string('x', 257);
        Assert.Throws<ArgumentException>(() => new Memory(tooLong, client));
    }

    [Fact]
    public void Construct_AcceptsMaxLengthEntityId()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        var max = new string('x', 256);
        var mem = new Memory(max, client);
        Assert.Equal(max, mem.EntityId);
    }

    [Fact]
    public async Task Construct_DiPath_DoesNotDisposeInjectedClient()
    {
        // Disposing a DI-constructed Memory must not dispose the caller's client.
        var handler = new RoutingHandler((_, __) => Json(new
        {
            node_id = 0,
            documents = 0,
            vectors = 0,
            version = "0.1.0",
        }));
        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        var mem = new Memory("e1", client);
        mem.Dispose();

        // The client is still usable (not disposed) — a call succeeds.
        var status = await client.StatusAsync();
        Assert.Equal(0, status.NodeId);
    }

    // ── Composition with a partition handle ──────────────────

    [Fact]
    public async Task Memory_OnPartitionHandle_SendsBothPartitionAndEntity()
    {
        // A Memory built on a partition handle scopes to BOTH partition and entity,
        // automatically — the Memory constructor is unchanged.
        var handler = new RoutingHandler((req, body) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/v1/documents" && req.Method == HttpMethod.Post)
            {
                return Json(new
                {
                    doc_id = "mem-1",
                    cid = "hash",
                    content_type = "text/plain",
                    size_bytes = 5,
                    chunks = 1,
                    vectors = 1,
                    version = 1,
                    entity_id = "patient-john",
                    created_at = "2026-06-15T12:00:00Z",
                });
            }
            // /search (recall → retrieve)
            return Json(new
            {
                query = "anx",
                results = new[]
                {
                    new { doc_id = "mem-1", score = 90, content = "Anxious about flying", content_type = "text/plain" },
                },
            });
        });

        var http = new HttpClient(handler);
        var client = new AetherClient(http, "http://localhost:9000");

        // Build the Memory on a partition-scoped client (DI constructor, unchanged).
        var mem = new Memory("patient-john", client.Partition("tenant-x"));

        await mem.RememberAsync("Anxious about flying");
        await mem.RecallAsync("anxiety");

        // remember → POST /documents carries both partition and entity_id on the query.
        var insert = handler.Requests.Single(r => r.Method == HttpMethod.Post && PathOf(r.Uri) == "/v1/documents");
        Assert.Contains("partition=tenant-x", insert.Uri.Query);
        Assert.Contains("entity_id=patient-john", insert.Uri.Query);

        // recall → GET /search carries both partition and entity_id on the query.
        var search = handler.Requests.Single(r => r.Method == HttpMethod.Get && PathOf(r.Uri) == "/v1/search");
        Assert.Contains("partition=tenant-x", search.Uri.Query);
        Assert.Contains("entity_id=patient-john", search.Uri.Query);
    }
}

/// <summary>
/// Contract tests for the <see cref="Memory"/> graph facade (Part II) covering
/// the cross-SDK memory contract. Mocked at the SAME transport layer as
/// <see cref="MemoryTests"/> (a stub <see cref="HttpMessageHandler"/>); the real
/// <see cref="AetherClient"/> runs and <see cref="Memory"/> wraps it via DI.
/// </summary>
public class MemoryGraphTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _route;

        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> route) => _route = route;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!, body));
            }
            return _route(request, body);
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static Memory CreateMemory(string entityId, RoutingHandler handler, AetherClient? client = null)
    {
        client ??= new AetherClient(new HttpClient(handler), "http://localhost:9000");
        return new Memory(entityId, client);
    }

    private static string PathOf(Uri uri) => uri.AbsolutePath;

    // Parses the JSON body of the request as a JsonElement for body-key assertions.
    private static JsonElement BodyJson(string? body) =>
        JsonSerializer.Deserialize<JsonElement>(body!);

    // Returns the decoded query of a Uri (so entity_id=… is readable verbatim).
    private static string Q(Uri uri) => Uri.UnescapeDataString(uri.Query);

    // ── Case 1: entity round-trip ─────────────────────────────────────────

    [Fact]
    public async Task UpsertEntity_PostsBodyAndScopesEntityId_ParsesResponse()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            memory_entity_id = "ent-1",
            entity_id = "owner-1",
            partition = (string?)null,
            entity_type = "person",
            display_name = "Jane",
            aliases = new[] { "J" },
            attributes = new { role = "admin", age = 30 },
            created_at = "2026-06-15T00:00:00Z",
            updated_at = "2026-06-15T00:00:00Z",
        }));

        var mem = CreateMemory("owner-1", handler);
        var ent = await mem.UpsertEntityAsync(
            "person",
            displayName: "Jane",
            attributes: new Dictionary<string, object?> { ["role"] = "admin", ["age"] = 30 });

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/memory/entities", PathOf(req.Uri));
        Assert.Contains("entity_id=owner-1", Q(req.Uri));

        // Body carries entity_type + provided fields.
        var body = BodyJson(req.Body);
        Assert.Equal("person", body.GetProperty("entity_type").GetString());
        Assert.Equal("Jane", body.GetProperty("display_name").GetString());
        Assert.Equal("admin", body.GetProperty("attributes").GetProperty("role").GetString());

        // Parsed result reflects the response (including memory_entity_id).
        Assert.Equal("ent-1", ent.MemoryEntityId);
        Assert.Equal("owner-1", ent.EntityId);
        Assert.Equal("person", ent.EntityType);
        Assert.Equal("Jane", ent.DisplayName);
        Assert.Equal(new[] { "J" }, ent.Aliases.ToArray());
        Assert.Equal("admin", ent.Attributes["role"]?.ToString());
        Assert.Equal("2026-06-15T00:00:00Z", ent.CreatedAt);
    }

    [Fact]
    public async Task UpsertEntity_OmitsUnsetOptionalBodyKeys()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            memory_entity_id = "ent-2",
            entity_id = "owner-1",
            entity_type = "project",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        await mem.UpsertEntityAsync("project");

        var body = BodyJson(handler.Requests.Single().Body);
        Assert.Equal("project", body.GetProperty("entity_type").GetString());
        // Unset optionals are omitted (engine uses serde(default)).
        Assert.False(body.TryGetProperty("memory_entity_id", out _));
        Assert.False(body.TryGetProperty("display_name", out _));
        Assert.False(body.TryGetProperty("aliases", out _));
        Assert.False(body.TryGetProperty("attributes", out _));
    }

    [Fact]
    public async Task GetEntity_GetsByIdInPath_ScopesEntityId()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            memory_entity_id = "ent 7/x",
            entity_id = "owner-1",
            entity_type = "person",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        var ent = await mem.GetEntityAsync("ent 7/x");

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        // Id is URL-escaped into the path (space -> %20, slash -> %2F) so it stays a
        // single path segment rather than being read as a sub-path.
        Assert.Equal("/v1/memory/entities/ent%207%2Fx", PathOf(req.Uri));
        Assert.Contains("entity_id=owner-1", Q(req.Uri));
        Assert.Equal("ent 7/x", ent.MemoryEntityId);
    }

    [Fact]
    public async Task GetEntity_RejectsEmptyId_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.GetEntityAsync(""));
        Assert.Empty(handler.Requests);
    }

    // ── Case 2: entity scoping + partition ────────────────────────────────

    [Fact]
    public async Task GraphRequest_OnPartitionHandle_SendsBothEntityAndPartition()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            memory_entity_id = "ent-1",
            entity_id = "owner-1",
            entity_type = "person",
            created_at = "t",
            updated_at = "t",
        }));

        var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");
        var mem = new Memory("owner-1", client.Partition("tenant-x"));

        await mem.UpsertEntityAsync("person");

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("entity_id=owner-1", q);
        Assert.Contains("partition=tenant-x", q);
    }

    // ── Case 3: list filters present/omitted ──────────────────────────────

    [Fact]
    public async Task ListEntities_SendsProvidedFilters()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            entities = new[]
            {
                new { memory_entity_id = "e1", entity_id = "owner-1", entity_type = "person", created_at = "t", updated_at = "t" },
            },
            count = 1,
        }));

        var mem = CreateMemory("owner-1", handler);
        var list = await mem.ListEntitiesAsync(entityType: "person", limit: 10);

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("entity_type=person", q);
        Assert.Contains("limit=10", q);
        Assert.Single(list);
        Assert.Equal("e1", list[0].MemoryEntityId);
    }

    [Fact]
    public async Task ListEntities_OmitsUnsetFilters()
    {
        var handler = new RoutingHandler((_, __) => Json(new { entities = Array.Empty<object>(), count = 0 }));
        var mem = CreateMemory("owner-1", handler);

        await mem.ListEntitiesAsync();

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("entity_id=owner-1", q);
        Assert.DoesNotContain("entity_type=", q);
        Assert.DoesNotContain("limit=", q);
    }

    // ── Case 4: relationship round-trip + active filter ───────────────────

    [Fact]
    public async Task Relate_PostsFromToType_ParsesResponse()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            relationship_id = "rel-1",
            entity_id = "owner-1",
            from_entity_id = "a",
            to_entity_id = "b",
            relationship_type = "works_at",
            attributes = new { since = "2020" },
            observed_at = "2026-06-15T00:00:00Z",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        var rel = await mem.RelateAsync("a", "b", "works_at",
            attributes: new Dictionary<string, object?> { ["since"] = "2020" },
            validFrom: "2020-01-01T00:00:00Z");

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/memory/relationships", PathOf(req.Uri));
        Assert.Contains("entity_id=owner-1", Q(req.Uri));

        var body = BodyJson(req.Body);
        Assert.Equal("a", body.GetProperty("from_entity_id").GetString());
        Assert.Equal("b", body.GetProperty("to_entity_id").GetString());
        Assert.Equal("works_at", body.GetProperty("relationship_type").GetString());
        Assert.Equal("2020-01-01T00:00:00Z", body.GetProperty("valid_from").GetString());

        Assert.Equal("rel-1", rel.RelationshipId);
        Assert.Equal("a", rel.FromEntityId);
        Assert.Equal("b", rel.ToEntityId);
        Assert.Equal("works_at", rel.RelationshipType);
    }

    [Fact]
    public async Task Relate_RejectsEmptyArgs_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.RelateAsync("", "b", "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => mem.RelateAsync("a", "  ", "t"));
        await Assert.ThrowsAsync<ArgumentException>(() => mem.RelateAsync("a", "b", ""));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListRelationships_SendsIncludeInactiveAndAsOf_DefaultOmits()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            relationships = Array.Empty<object>(),
            count = 0,
        }));

        var mem = CreateMemory("owner-1", handler);

        // With include_inactive + as_of.
        await mem.ListRelationshipsAsync(includeInactive: true, asOf: "2026-06-15T00:00:00Z");
        var q1 = Q(handler.Requests.Last().Uri);
        Assert.Contains("include_inactive=true", q1);
        Assert.Contains("as_of=2026-06-15T00:00:00Z", q1);

        // Default omits include_inactive.
        await mem.ListRelationshipsAsync();
        var q2 = Q(handler.Requests.Last().Uri);
        Assert.DoesNotContain("include_inactive", q2);
        Assert.DoesNotContain("as_of", q2);
    }

    // ── Case 5: fact assert + subject ─────────────────────────────────────

    [Fact]
    public async Task RememberFact_OwnerDefault_PostsSubjectTypeAndScalarValue()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            fact_id = "f1",
            entity_id = "owner-1",
            subject_type = "owner",
            predicate = "favorite_color",
            value = "blue",
            cardinality = "single",
            observed_at = "t",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        var fact = await mem.RememberFactAsync("favorite_color", "blue");

        var req = handler.Requests.Single();
        Assert.Equal("/v1/memory/facts", PathOf(req.Uri));
        var body = BodyJson(req.Body);
        Assert.Equal("owner", body.GetProperty("subject_type").GetString());
        Assert.Equal("favorite_color", body.GetProperty("predicate").GetString());
        Assert.Equal("blue", body.GetProperty("value").GetString());
        // owner subject → no subject_id key.
        Assert.False(body.TryGetProperty("subject_id", out _));

        Assert.Equal("f1", fact.FactId);
        Assert.Equal("blue", fact.Value?.ToString());
    }

    [Fact]
    public async Task RememberFact_EntitySubject_PostsBothSubjectFields()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            fact_id = "f2",
            entity_id = "owner-1",
            subject_type = "entity",
            subject_id = "E",
            predicate = "status",
            value = "active",
            cardinality = "single",
            observed_at = "t",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        await mem.RememberFactAsync("status", "active", subjectType: "entity", subjectId: "E");

        var body = BodyJson(handler.Requests.Single().Body);
        Assert.Equal("entity", body.GetProperty("subject_type").GetString());
        Assert.Equal("E", body.GetProperty("subject_id").GetString());
        Assert.Equal("status", body.GetProperty("predicate").GetString());
    }

    [Theory]
    [InlineData(42)]
    [InlineData(3.14)]
    [InlineData(true)]
    public async Task RememberFact_ScalarValueTypes_SerializeNatively(object value)
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            fact_id = "f",
            entity_id = "owner-1",
            subject_type = "owner",
            predicate = "p",
            value = (object?)null,
            cardinality = "single",
            observed_at = "t",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        await mem.RememberFactAsync("p", value);

        var body = BodyJson(handler.Requests.Single().Body);
        var valueEl = body.GetProperty("value");
        // The value serializes with its native JSON kind (number/bool), not a string.
        switch (value)
        {
            case int i:
                Assert.Equal(JsonValueKind.Number, valueEl.ValueKind);
                Assert.Equal(i, valueEl.GetInt32());
                break;
            case double d:
                Assert.Equal(JsonValueKind.Number, valueEl.ValueKind);
                Assert.Equal(d, valueEl.GetDouble(), 9);
                break;
            case bool b:
                Assert.Equal(b ? JsonValueKind.True : JsonValueKind.False, valueEl.ValueKind);
                break;
        }
    }

    [Fact]
    public async Task RememberFact_NullValue_SentAsExplicitJsonNull()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            fact_id = "f",
            entity_id = "owner-1",
            subject_type = "owner",
            predicate = "p",
            value = (object?)null,
            cardinality = "single",
            observed_at = "t",
            created_at = "t",
            updated_at = "t",
        }));

        var mem = CreateMemory("owner-1", handler);
        await mem.RememberFactAsync("p", null);

        var body = BodyJson(handler.Requests.Single().Body);
        // `value` is ALWAYS present, even when null — it must be an explicit JSON
        // null (the key is NOT dropped).
        Assert.True(body.TryGetProperty("value", out var v));
        Assert.Equal(JsonValueKind.Null, v.ValueKind);
    }

    [Fact]
    public async Task RememberFact_NonOwnerWithoutSubjectId_ThrowsNoHttp()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberFactAsync("status", "x", subjectType: "entity"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberFactAsync("status", "x", subjectType: "relationship", subjectId: ""));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RememberFact_RejectsEmptyPredicateOrBadEnums_NoHttp()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.RememberFactAsync("", "v"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberFactAsync("p", "v", subjectType: "bogus"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            mem.RememberFactAsync("p", "v", cardinality: "weird"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListFacts_SendsFilters_AndValidatesSubject()
    {
        var handler = new RoutingHandler((_, __) => Json(new { facts = Array.Empty<object>(), count = 0 }));
        var mem = CreateMemory("owner-1", handler);

        await mem.ListFactsAsync(subjectType: "entity", subjectId: "E", predicate: "status",
            includeInactive: true, asOf: "2026-06-15T00:00:00Z", limit: 7);

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("subject_type=entity", q);
        Assert.Contains("subject_id=E", q);
        Assert.Contains("predicate=status", q);
        Assert.Contains("include_inactive=true", q);
        Assert.Contains("as_of=2026-06-15T00:00:00Z", q);
        Assert.Contains("limit=7", q);
    }

    [Fact]
    public async Task ListFacts_NonOwnerSubjectWithoutId_ThrowsNoHttp()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.ListFactsAsync(subjectType: "entity"));
        Assert.Empty(handler.Requests);
    }

    // ── Case 6: fact history ──────────────────────────────────────────────

    [Fact]
    public async Task FactHistory_SendsHistoryTrueWithSubjectAndPredicate()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            facts = new[]
            {
                new { fact_id = "old", entity_id = "owner-1", subject_type = "entity", subject_id = "E", predicate = "status", value = "a", cardinality = "single", observed_at = "t", created_at = "t", updated_at = "t" },
                new { fact_id = "new", entity_id = "owner-1", subject_type = "entity", subject_id = "E", predicate = "status", value = "b", cardinality = "single", observed_at = "t", created_at = "t", updated_at = "t" },
            },
            count = 2,
        }));

        var mem = CreateMemory("owner-1", handler);
        var history = await mem.FactHistoryAsync("status", subjectType: "entity", subjectId: "E");

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("history=true", q);
        Assert.Contains("subject_type=entity", q);
        Assert.Contains("subject_id=E", q);
        Assert.Contains("predicate=status", q);
        Assert.Equal(2, history.Count);
        Assert.Equal("old", history[0].FactId);
    }

    [Fact]
    public async Task FactHistory_OwnerSubject_OmitsSubjectId()
    {
        var handler = new RoutingHandler((_, __) => Json(new { facts = Array.Empty<object>(), count = 0 }));
        var mem = CreateMemory("owner-1", handler);

        await mem.FactHistoryAsync("status");

        var q = Q(handler.Requests.Single().Uri);
        Assert.Contains("history=true", q);
        Assert.Contains("subject_type=owner", q);
        Assert.DoesNotContain("subject_id=", q);
    }

    // ── Case 7: consolidate ───────────────────────────────────────────────

    [Fact]
    public async Task Consolidate_PostsAndParsesReport()
    {
        var handler = new RoutingHandler((_, __) => Json(new
        {
            active_facts_before = 10,
            active_facts_after = 7,
            retracted = 3,
        }));

        var mem = CreateMemory("owner-1", handler);
        var report = await mem.ConsolidateAsync();

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/memory/consolidate", PathOf(req.Uri));
        Assert.Contains("entity_id=owner-1", Q(req.Uri));
        // No body.
        Assert.True(string.IsNullOrEmpty(req.Body));

        Assert.Equal(10, report.ActiveFactsBefore);
        Assert.Equal(7, report.ActiveFactsAfter);
        Assert.Equal(3, report.Retracted);
    }

    // ── Case 8: error passthrough ─────────────────────────────────────────

    [Fact]
    public async Task UpsertEntity_SurfacesTypedBillingError_Unchanged()
    {
        var handler = new RoutingHandler((_, __) => Json(
            new { error = "credit exhausted", code = "credit_exhausted" },
            (HttpStatusCode)402));

        var mem = CreateMemory("owner-1", handler);

        var ex = await Assert.ThrowsAsync<CreditExhaustedException>(() => mem.UpsertEntityAsync("person"));
        Assert.Equal((HttpStatusCode)402, ex.StatusCode);
        Assert.Equal("credit_exhausted", ex.ErrorCode);
    }

    [Fact]
    public async Task RememberFact_SurfacesGenericApiError_Unchanged()
    {
        var handler = new RoutingHandler((_, __) => Json(
            new { error = "bad request" }, HttpStatusCode.BadRequest));
        var mem = CreateMemory("owner-1", handler);

        var ex = await Assert.ThrowsAsync<AetherApiException>(() => mem.RememberFactAsync("p", "v"));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    // ── Case 9: invalid arguments (already covered above per method) ──────

    [Fact]
    public async Task UpsertEntity_RejectsEmptyEntityType_NoHttp()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var mem = CreateMemory("owner-1", handler);

        await Assert.ThrowsAsync<ArgumentException>(() => mem.UpsertEntityAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => mem.UpsertEntityAsync("   "));
        Assert.Empty(handler.Requests);
    }
}
