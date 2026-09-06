using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aether.Sdk.Tests;

public class ThreadTests
{
    private static AetherClient CreateClient(MockHttpMessageHandler handler) =>
        new(new HttpClient(handler), "http://localhost:9000");

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Bytes(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };

    private sealed class ThreadContextHandler : HttpMessageHandler
    {
        private readonly int _documentCount;
        private readonly string _downloadText;
        private readonly int _delayMilliseconds;
        private int _activeDownloads;
        private int _peakDownloads;
        private int _downloadCalls;

        public int PeakDownloads => _peakDownloads;
        public int DownloadCalls => _downloadCalls;

        public ThreadContextHandler(int documentCount, string downloadText, int delayMilliseconds)
        {
            _documentCount = documentCount;
            _downloadText = downloadText;
            _delayMilliseconds = delayMilliseconds;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/threads/care%2F42")
            {
                return Json(new
                {
                    thread_id = "care/42",
                    documents = Enumerable.Range(0, _documentCount).Select(index => new
                    {
                        doc_id = $"turn-{index}",
                        entity_id = "patient-42",
                        thread_id = "care/42",
                        turn_index = (ulong)index,
                    }),
                });
            }
            if (path == "/v1/search")
                return Json(new { query = "history", results = Array.Empty<object>() });
            if (path.EndsWith("/download", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _downloadCalls);
                var active = Interlocked.Increment(ref _activeDownloads);
                while (true)
                {
                    var peak = Volatile.Read(ref _peakDownloads);
                    if (active <= peak ||
                        Interlocked.CompareExchange(ref _peakDownloads, active, peak) == peak)
                        break;
                }
                try
                {
                    if (_delayMilliseconds > 0)
                        await Task.Delay(_delayMilliseconds, cancellationToken).ConfigureAwait(false);
                    return Bytes(_downloadText);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeDownloads);
                }
            }
            throw new Xunit.Sdk.XunitException($"Unexpected request {request.RequestUri}");
        }
    }

    [Fact]
    public async Task AppendThread_UsesVersionedEscapedPathAndCallerIdempotencyKey()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            doc_id = "turn-1",
            cid = "blake3:turn-1",
            chunks = 1,
            vectors = 1,
            version = 1,
            content_type = "text/plain",
            size_bytes = 5,
            thread_id = "chat/42",
            turn_index = 0UL,
        }, HttpStatusCode.Created);
        using var client = CreateClient(handler);

        var result = await client.AppendThreadAsync("chat/42", new ThreadAppendRequest
        {
            Text = "hello",
            Tags = new List<string> { "support" },
            IdempotencyKey = "thread-turn-42-0",
        });

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(
            "v1/threads/chat%2F42/append",
            handler.LastRequest.RequestUri!.GetComponents(UriComponents.Path, UriFormat.UriEscaped));
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(
            "thread-turn-42-0",
            handler.LastRequest.Headers.GetValues("Idempotency-Key").Single());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("hello", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("support", body.RootElement.GetProperty("tags")[0].GetString());
        Assert.False(body.RootElement.TryGetProperty("idempotency_key", out _));
        Assert.Equal((ulong)0, result.TurnIndex);
    }

    [Fact]
    public async Task AppendThread_EmptyIdempotencyKeyMintsOneStableAcrossRetries()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.Created);
        using var client = new AetherClient(new HttpClient(handler), "http://localhost:9000");

        await client.AppendThreadAsync("chat-1", new ThreadAppendRequest
        {
            Text = "hello",
            IdempotencyKey = "",
        });

        Assert.Equal(2, handler.Requests.Count);
        var first = handler.Requests[0].Headers.GetValues("Idempotency-Key").Single();
        var second = handler.Requests[1].Headers.GetValues("Idempotency-Key").Single();
        Assert.False(string.IsNullOrEmpty(first));
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetThread_ForwardsWindowAndPartition()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            thread_id = "chat-1",
            documents = new[]
            {
                new { doc_id = "turn-2", cid = "blake3:turn-2", thread_id = "chat-1", turn_index = 2UL },
            },
        });
        using var baseClient = CreateClient(handler);
        var client = baseClient.Partition("tenant-a");

        var thread = await client.GetThreadAsync("chat-1", new ThreadReadOptions
        {
            LastNTurns = 3,
            RecentFirst = true,
        });

        Assert.Equal("/v1/threads/chat-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        var query = handler.LastRequest.RequestUri.Query;
        Assert.Contains("last_n_turns=3", query);
        Assert.Contains("recent_first=true", query);
        Assert.Contains("partition=tenant-a", query);
        Assert.Single(thread.Documents);
        Assert.Equal((ulong)2, thread.Documents[0].TurnIndex);
    }

    [Fact]
    public async Task ThreadArgumentsAreValidatedBeforeTransport()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AppendThreadAsync(" ", new ThreadAppendRequest { Text = "hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AppendThreadAsync("safe\0id", new ThreadAppendRequest { Text = "hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AppendThreadAsync("bad\uD800id", new ThreadAppendRequest { Text = "hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetThreadAsync("bad\uDC00id"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetThreadAsync(string.Concat(Enumerable.Repeat("😀", 257))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetThreadAsync("chat", new ThreadReadOptions { LastNTurns = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.GetThreadAsync("chat", new ThreadReadOptions { LastNTurns = 1001 }));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ThreadIdLengthCountsUnicodeScalars()
    {
        var id = string.Concat(Enumerable.Repeat("😀", 256));
        var handler = MockHttpMessageHandler.WithJson(new
        {
            thread_id = id,
            documents = Array.Empty<object>(),
        });
        using var client = CreateClient(handler);

        await client.GetThreadAsync(id);

        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task SearchAsync_ForwardsThreadFilter()
    {
        var handler = MockHttpMessageHandler.WithJson(new { query = "hello", results = Array.Empty<object>() });
        using var client = CreateClient(handler);

        await client.SearchAsync("hello", threadId: "chat/42");

        Assert.Contains("thread_id=chat%2F42", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task MemoryThread_AppendScopesEntityAndMetadata()
    {
        var handler = new MockHttpMessageHandler(_ => Json(new
        {
            doc_id = "turn-1",
            cid = "blake3:turn-1",
            content_type = "text/plain",
            thread_id = "care/42",
            turn_index = 0UL,
            entity_id = "patient-42",
            created_at = "2026-07-10T12:00:00Z",
            metadata = new { role = "patient" },
        }, HttpStatusCode.Created));
        using var client = CreateClient(handler);
        using var memory = new Memory("patient-42", client);
        var direct = new Aether.Sdk.Thread(memory, "care/42");
        Assert.IsType<Aether.Sdk.Thread>(memory.Thread("care/42"));

        var item = await direct.AppendAsync(
            "I slept better",
            new Dictionary<string, object?> { ["role"] = "patient" });

        Assert.Equal("turn-1", item.Id);
        Assert.Equal("I slept better", item.Text);
        Assert.Equal("patient-42", item.EntityId);
        Assert.Equal("patient", item.Metadata["role"]?.ToString());
        Assert.Equal(
            "v1/threads/care%2F42/append",
            handler.LastRequest!.RequestUri!.GetComponents(UriComponents.Path, UriFormat.UriEscaped));
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("patient-42", body.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal("patient", body.RootElement.GetProperty("metadata").GetProperty("role").GetString());
    }

    [Fact]
    public async Task MemoryThread_ContextReturnsRecentThenSemanticAndDeduplicates()
    {
        var requests = new List<Uri>();
        var requestLock = new object();
        var handler = new MockHttpMessageHandler(request =>
        {
            lock (requestLock)
                requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/threads/care%2F42" => Json(new
                {
                    thread_id = "care/42",
                    documents = new object[]
                    {
                        new
                        {
                            doc_id = "recent-1",
                            entity_id = "patient-42",
                            thread_id = "care/42",
                            turn_index = 3UL,
                            created_at = "2026-07-10T12:00:00Z",
                        },
                        new
                        {
                            doc_id = "other-owner",
                            entity_id = "patient-99",
                            thread_id = "care/42",
                            turn_index = 4UL,
                        },
                    },
                }),
                "/v1/search" => Json(new
                {
                    query = "what helped?",
                    results = new object[]
                    {
                        new
                        {
                            doc_id = "recent-1",
                            score = 95,
                            content = "duplicate",
                            entity_id = "patient-42",
                            thread_id = "care/42",
                        },
                        new
                        {
                            doc_id = "semantic-1",
                            score = 75,
                            content = "earlier coping plan",
                            entity_id = "patient-42",
                            thread_id = "care/42",
                            metadata = new { kind = "plan" },
                        },
                    },
                }),
                "/v1/documents/recent-1/download" => Bytes("latest check-in"),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected request {request.RequestUri}"),
            };
        });
        using var client = CreateClient(handler);
        using var memory = new Memory("patient-42", client);

        var items = await memory.Thread("care/42").ContextAsync(
            "what helped?", lastNTurns: 2, recentFirst: true);

        Assert.Equal(new[] { "recent-1", "semantic-1" }, items.Select(item => item.Id));
        Assert.Equal(new[] { "latest check-in", "earlier coping plan" }, items.Select(item => item.Text));
        Assert.Equal(0.75, items[1].Score);
        var threadRequest = requests.Single(uri => uri.AbsolutePath.Contains("/threads/"));
        Assert.Contains("last_n_turns=2", threadRequest.Query);
        Assert.Contains("recent_first=true", threadRequest.Query);
        var searchRequest = requests.Single(uri => uri.AbsolutePath == "/v1/search");
        Assert.Contains("k=5", searchRequest.Query);
        Assert.Contains("entity_id=patient-42", searchRequest.Query);
        Assert.Contains("thread_id=care%2F42", searchRequest.Query);
    }

    [Fact]
    public async Task MemoryThread_ContextRetriesPendingOriginProjection()
    {
        var downloadAttempts = 0;
        var handler = new MockHttpMessageHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/threads/care%2F42":
                    return Json(new
                    {
                        thread_id = "care/42",
                        documents = new[]
                        {
                            new
                            {
                                doc_id = "pending-1",
                                entity_id = "patient-42",
                                thread_id = "care/42",
                                turn_index = 0UL,
                            },
                        },
                    });
                case "/v1/search":
                    return Json(new { query = "history", results = Array.Empty<object>() });
                case "/v1/documents/pending-1/download":
                    if (Interlocked.Increment(ref downloadAttempts) == 1)
                    {
                        var pending = Json(new
                        {
                            error = "Thread turn is committed but its origin projection is still pending",
                            code = "thread_projection_pending",
                        }, HttpStatusCode.ServiceUnavailable);
                        pending.Headers.TryAddWithoutValidation("Retry-After", "0");
                        return pending;
                    }
                    return Bytes("projected turn");
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected request {request.RequestUri}");
            }
        });
        using var client = CreateClient(handler);
        using var memory = new Memory("patient-42", client);

        var items = await memory.Thread("care/42").ContextAsync("history");

        Assert.Single(items);
        Assert.Equal("projected turn", items[0].Text);
        Assert.Equal(2, downloadAttempts);
    }

    [Fact]
    public async Task MemoryThread_ContextBoundsConcurrentDownloads()
    {
        var handler = new ThreadContextHandler(17, "turn text", delayMilliseconds: 5);
        using var http = new HttpClient(handler);
        using var client = new AetherClient(http, "http://localhost:9000");
        using var memory = new Memory("patient-42", client);

        var items = await memory.Thread("care/42").ContextAsync("history", lastNTurns: 17);

        Assert.Equal(17, items.Count);
        Assert.Equal(8, handler.PeakDownloads);
    }

    [Fact]
    public async Task MemoryThread_ContextStopsBeforeNextBatchAtByteBudget()
    {
        var handler = new ThreadContextHandler(
            9,
            new string('x', 2 * 1024 * 1024 + 1),
            delayMilliseconds: 0);
        using var http = new HttpClient(handler);
        using var client = new AetherClient(http, "http://localhost:9000");
        using var memory = new Memory("patient-42", client);

        var error = await Assert.ThrowsAsync<AetherException>(() =>
            memory.Thread("care/42").ContextAsync("history", lastNTurns: 9));

        Assert.Contains("byte safety limit", error.Message);
        Assert.Equal(8, handler.DownloadCalls);
    }

    [Fact]
    public async Task MemoryThread_ValidatesBeforeTransport()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);
        using var memory = new Memory("patient-42", client);

        Assert.Throws<ArgumentException>(() => memory.Thread(" "));
        Assert.Throws<ArgumentException>(() => memory.Thread("."));
        Assert.Throws<ArgumentException>(() => memory.Thread(".."));
        Assert.Throws<ArgumentException>(() => memory.Thread("bad\uD800id"));
        Assert.Throws<ArgumentException>(() => new Aether.Sdk.Thread(memory, "bad\uDC00id"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            memory.Thread("care").AppendAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            memory.Thread("care").ContextAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            memory.Thread("care").ContextAsync("query", lastNTurns: 0));
        Assert.Null(handler.LastRequest);
    }

    // ── Whole-thread lifecycle ────────────────────────────────

    private static string EscapedPath(HttpRequestMessage request) =>
        request.RequestUri!.GetComponents(UriComponents.Path, UriFormat.UriEscaped);

    private static string IdempotencyKey(HttpRequestMessage request) =>
        request.Headers.GetValues("Idempotency-Key").Single();

    [Fact]
    public async Task ThreadRestore_PostsEscapedPathWithPartitionAndIdempotencyKey()
    {
        var handler = MockHttpMessageHandler.WithJson(new
        {
            status = "restored",
            thread_id = "care/42",
            turns = 3,
        });
        using var baseClient = CreateClient(handler);
        var client = baseClient.Partition("tenant-a");

        var result = await client.ThreadRestoreAsync("care/42");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/restore", EscapedPath(handler.LastRequest));
        Assert.Contains("partition=tenant-a", handler.LastRequest.RequestUri!.Query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));
        Assert.Equal("restored", result.Status);
        Assert.Equal("care/42", result.ThreadId);
        Assert.Equal(3, result.Turns);
    }

    [Fact]
    public async Task ThreadRestore_HonorsCallerIdempotencyKey()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "restored", thread_id = "chat-1", turns = 1 });
        using var client = CreateClient(handler);

        await client.ThreadRestoreAsync("chat-1", idempotencyKey: "restore-key-1");

        Assert.Equal("restore-key-1", IdempotencyKey(handler.LastRequest!));
    }

    [Fact]
    public async Task ThreadAcl_PutsAclReadersBodyAlwaysPresentWithIdempotencyKey()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "acl_updated", thread_id = "care/42", turns = 4 });
        using var baseClient = CreateClient(handler);
        var client = baseClient.Partition("tenant-a");

        // Restrict to a reader set.
        var restricted = await client.ThreadAclAsync("care/42", new[] { "clinician-7" });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/acl", EscapedPath(handler.LastRequest));
        Assert.Contains("partition=tenant-a", handler.LastRequest.RequestUri!.Query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            var readers = body.RootElement.GetProperty("acl_readers");
            Assert.Equal(JsonValueKind.Array, readers.ValueKind);
            Assert.Equal("clinician-7", readers[0].GetString());
        }
        Assert.Equal("acl_updated", restricted.Status);
        Assert.Equal(4, restricted.Turns);

        // Empty list = admin-only quarantine: still sent as [].
        await client.ThreadAclAsync("care/42", System.Array.Empty<string>());
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            var readers = body.RootElement.GetProperty("acl_readers");
            Assert.Equal(JsonValueKind.Array, readers.ValueKind);
            Assert.Equal(0, readers.GetArrayLength());
        }

        // Null = unlabel: the field is ALWAYS present, as an explicit JSON null.
        await client.ThreadAclAsync("care/42", null);
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.True(body.RootElement.TryGetProperty("acl_readers", out var readers));
            Assert.Equal(JsonValueKind.Null, readers.ValueKind);
        }
    }

    [Fact]
    public async Task ThreadMove_PostsBothPartitionFieldsWithIdempotencyKey_NotAutoScoped()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "moved", thread_id = "care/42", turns = 5 });
        using var baseClient = CreateClient(handler);
        // A partition handle must NOT leak into the move — it names partitions in the body.
        var client = baseClient.Partition("tenant-x");

        var result = await client.ThreadMoveAsync("care/42", "client-a", "client-b");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/move", EscapedPath(handler.LastRequest));
        Assert.DoesNotContain("partition=", handler.LastRequest.RequestUri!.Query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("client-a", body.RootElement.GetProperty("expect_partition").GetString());
        Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
        Assert.Equal("moved", result.Status);
        Assert.Equal(5, result.Turns);
    }

    [Fact]
    public async Task ThreadMove_NullNamesTheDefaultPartition_ExplicitNullOnWire()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "moved", thread_id = "chat-1", turns = 2 });
        using var client = CreateClient(handler);

        await client.ThreadMoveAsync("chat-1", null, "client-b");
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("expect_partition").ValueKind);
            Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
        }

        await client.ThreadMoveAsync("chat-1", "client-a", null);
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal("client-a", body.RootElement.GetProperty("expect_partition").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("to_partition").ValueKind);
        }
    }

    [Fact]
    public async Task ThreadDelete_SoftTombstoneAndHardErase_WithPartitionAndIdempotencyKey()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", thread_id = "care/42", turns = 6 });
        using var baseClient = CreateClient(handler);
        var client = baseClient.Partition("tenant-a");

        // Soft tombstone (default): no ?hard, partition guard present.
        var soft = await client.ThreadDeleteAsync("care/42");
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42", EscapedPath(handler.LastRequest));
        Assert.DoesNotContain("hard", handler.LastRequest.RequestUri!.Query);
        Assert.Contains("partition=tenant-a", handler.LastRequest.RequestUri.Query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));
        Assert.Equal("tombstoned", soft.Status);
        Assert.Equal(6, soft.Turns);

        // Hard erase: ?hard=true AND the partition guard, both present.
        await client.ThreadDeleteAsync("care/42", hard: true);
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("hard=true", query);
        Assert.Contains("partition=tenant-a", query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));
    }

    [Fact]
    public async Task ThreadDelete_UnscopedClient_SendsNoPartition()
    {
        var handler = MockHttpMessageHandler.WithJson(new { status = "tombstoned", thread_id = "chat-1", turns = 1 });
        using var client = CreateClient(handler);

        await client.ThreadDeleteAsync("chat-1");

        Assert.DoesNotContain("partition", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task ThreadLifecycle_EmptyIdempotencyKeyMintsOneStableAcrossRetries_NonPostMethods()
    {
        // The ACL route is a PUT and the delete route is a DELETE; both still send
        // a minted, retry-stable Idempotency-Key even though only POST auto-mints.
        var aclHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)
        {
            ResponseBody = new { status = "acl_updated", thread_id = "chat-1", turns = 1 },
        };
        using (var client = new AetherClient(new HttpClient(aclHandler), "http://localhost:9000"))
        {
            await client.ThreadAclAsync("chat-1", new[] { "reader-1" });
            Assert.Equal(2, aclHandler.Requests.Count);
            var first = IdempotencyKey(aclHandler.Requests[0]);
            var second = IdempotencyKey(aclHandler.Requests[1]);
            Assert.False(string.IsNullOrEmpty(first));
            Assert.True(Guid.TryParse(first, out _));
            Assert.Equal(first, second);
        }

        var deleteHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK)
        {
            ResponseBody = new { status = "tombstoned", thread_id = "chat-1", turns = 1 },
        };
        using (var client = new AetherClient(new HttpClient(deleteHandler), "http://localhost:9000"))
        {
            await client.ThreadDeleteAsync("chat-1");
            Assert.Equal(2, deleteHandler.Requests.Count);
            var first = IdempotencyKey(deleteHandler.Requests[0]);
            var second = IdempotencyKey(deleteHandler.Requests[1]);
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public async Task ThreadLifecycle_ValidatesBeforeTransport()
    {
        var handler = MockHttpMessageHandler.WithJson(new { });
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ThreadRestoreAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ThreadAclAsync("bad\uD800id", null));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ThreadDeleteAsync("."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ThreadMoveAsync("chat", "  ", "client-b"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ThreadMoveAsync("chat", "client-a", new string('a', 257)));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task MemoryThread_LifecycleFacadeMirrorsRawClient()
    {
        var handler = new MockHttpMessageHandler(request =>
            Json(new
            {
                status = "ok",
                thread_id = "care/42",
                turns = 2,
            }));
        using var baseClient = CreateClient(handler);
        var scoped = baseClient.Partition("tenant-a");
        using var memory = new Memory("patient-42", scoped);
        var thread = memory.Thread("care/42");

        await thread.RestoreAsync();
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/restore", EscapedPath(handler.LastRequest));
        Assert.Contains("partition=tenant-a", handler.LastRequest.RequestUri!.Query);
        Assert.False(string.IsNullOrEmpty(IdempotencyKey(handler.LastRequest)));

        await thread.SetAclAsync(new[] { "clinician-7" });
        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/acl", EscapedPath(handler.LastRequest));
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
            Assert.Equal("clinician-7", body.RootElement.GetProperty("acl_readers")[0].GetString());

        // The facade asserts the thread's CURRENT partition (the client scope) as
        // expect_partition and moves it to the destination.
        await thread.MoveAsync("client-b");
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42/move", EscapedPath(handler.LastRequest));
        using (var body = JsonDocument.Parse(handler.LastRequestBody!))
        {
            Assert.Equal("tenant-a", body.RootElement.GetProperty("expect_partition").GetString());
            Assert.Equal("client-b", body.RootElement.GetProperty("to_partition").GetString());
        }

        await thread.DeleteAsync();
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("v1/threads/care%2F42", EscapedPath(handler.LastRequest));
        Assert.DoesNotContain("hard", handler.LastRequest.RequestUri!.Query);

        await thread.DeleteAsync(hard: true);
        Assert.Contains("hard=true", handler.LastRequest!.RequestUri!.Query);
    }
}
