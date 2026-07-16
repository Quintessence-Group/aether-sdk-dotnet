using System.Net;
using System.Text;
using System.Text.Json;
using Aether.Sdk;
using Aether.Sdk.SemanticKernel;
using Microsoft.Extensions.AI;
using Xunit;

namespace Aether.Sdk.SemanticKernel.Tests;

/// <summary>
/// Contract tests for <see cref="AetherMemoryProvider"/>, the Semantic Kernel
/// context provider that gives an agent Aether-backed memory.
///
/// These mock the same transport layer as the core SDK's memory tests (a stub
/// <see cref="HttpMessageHandler"/>); the real <see cref="AetherClient"/> and
/// <see cref="Memory"/> run underneath it and nothing in
/// <see cref="AetherMemoryProvider"/> itself is mocked.
/// </summary>
public class AetherMemoryProviderTests
{
    // A recording handler that routes by path so a single provider call can be
    // served deterministically and every outgoing request inspected afterward.
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

    private static string PathOf(Uri uri) => uri.AbsolutePath;

    // Aether.Sdk exposes an internal HttpClient-injecting constructor as its
    // testing hook (the public constructor has no way to supply a custom
    // HttpMessageHandler). This project is granted access via InternalsVisibleTo
    // in Aether.Sdk.csproj — the same way the core Aether.Sdk.Tests project stands
    // up a real AetherClient over a stub transport.
    private static AetherClient CreateClient(HttpMessageHandler handler, string baseUrl = "http://localhost:9000")
    {
        var http = new HttpClient(handler);
        return new AetherClient(http, baseUrl);
    }

    private static AetherMemoryProvider CreateProvider(
        string entityId, RoutingHandler handler, AetherMemoryProviderOptions? options = null)
    {
        var client = CreateClient(handler);
        return new AetherMemoryProvider(client, entityId, options);
    }

    // The response an insert (remember) call gets back.
    private static HttpResponseMessage InsertResponse(string entityId) => Json(new
    {
        doc_id = "mem-1",
        cid = "hash",
        content_type = "text/plain",
        size_bytes = 5,
        chunks = 1,
        vectors = 1,
        version = 1,
        entity_id = entityId,
        created_at = "2026-06-15T12:00:00Z",
    });

    // The response a search (recall) call gets back: each hit is (doc id, 0-100
    // wire score, remembered text).
    private static HttpResponseMessage SearchResponse(params (string DocId, int Score, string Content)[] hits) =>
        Json(new
        {
            query = "q",
            results = hits.Select(h => new
            {
                doc_id = h.DocId,
                score = h.Score,
                content = h.Content,
                content_type = "text/plain",
            }).ToArray(),
        });

    // ── MessageAddingAsync: what gets remembered ────────────────────────

    [Fact]
    public async Task UserMessage_IsRemembered_SendsOneInsertWithEntityIdAndText()
    {
        var handler = new RoutingHandler((_, __) => InsertResponse("e1"));
        var provider = CreateProvider("e1", handler);

        await provider.MessageAddingAsync(
            null, new ChatMessage(ChatRole.User, "I enjoy long walks on the beach"), CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/documents", PathOf(req.Uri));
        Assert.Contains("entity_id=e1", req.Uri.Query);
        // The message text is the raw insert body (not JSON, not a query param).
        Assert.Equal("I enjoy long walks on the beach", req.Body);
    }

    [Fact]
    public async Task AssistantMessage_DefaultOptions_NotRemembered_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => InsertResponse("e1"));
        var provider = CreateProvider("e1", handler);

        await provider.MessageAddingAsync(
            null, new ChatMessage(ChatRole.Assistant, "Sure, happy to help with that."), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AssistantMessage_IncludeAssistantMessagesTrue_IsRemembered()
    {
        var handler = new RoutingHandler((_, __) => InsertResponse("e1"));
        var options = new AetherMemoryProviderOptions { IncludeAssistantMessages = true };
        var provider = CreateProvider("e1", handler, options);

        await provider.MessageAddingAsync(
            null, new ChatMessage(ChatRole.Assistant, "Sure, happy to help with that."), CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/v1/documents", PathOf(req.Uri));
    }

    [Fact]
    public async Task SystemMessage_NeverRemembered_NoHttpCall()
    {
        // IncludeAssistantMessages=true shows system messages are ignored
        // unconditionally, not merely because a flag was left off.
        var handler = new RoutingHandler((_, __) => InsertResponse("e1"));
        var options = new AetherMemoryProviderOptions { IncludeAssistantMessages = true };
        var provider = CreateProvider("e1", handler, options);

        await provider.MessageAddingAsync(
            null, new ChatMessage(ChatRole.System, "You are a helpful assistant."), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EmptyOrWhitespaceUserMessage_NotRemembered_NoHttpCall()
    {
        var handler = new RoutingHandler((_, __) => InsertResponse("e1"));
        var provider = CreateProvider("e1", handler);

        await provider.MessageAddingAsync(null, new ChatMessage(ChatRole.User, ""), CancellationToken.None);
        await provider.MessageAddingAsync(null, new ChatMessage(ChatRole.User, "   "), CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    // ── ModelInvokingAsync: what gets recalled and injected ─────────────

    [Fact]
    public async Task ModelInvoking_UserMessageWithHits_ReturnsInstructionsWithBothTexts_OneSearchCall()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            Assert.Equal("/v1/search", PathOf(req.RequestUri!));
            return SearchResponse(
                ("r1", 90, "enjoys hiking on weekends"),
                ("r2", 80, "has a cat named Whiskers"));
        });
        var options = new AetherMemoryProviderOptions();
        var provider = CreateProvider("e1", handler, options);
        var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "what do you know about me?") };

        var context = await provider.ModelInvokingAsync(messages, CancellationToken.None);

        Assert.NotNull(context.Instructions);
        Assert.StartsWith(options.ContextPrompt, context.Instructions);
        Assert.Contains("enjoys hiking on weekends", context.Instructions);
        Assert.Contains("has a cat named Whiskers", context.Instructions);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ModelInvoking_NoUserText_ReturnsEmptyContext_NoSearchCall()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            Assert.Fail($"unexpected HTTP call to {PathOf(req.RequestUri!)}; there is no user text to search on");
            return Json(new { });
        });
        var provider = CreateProvider("e1", handler);
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.Assistant, "How can I help today?"),
        };

        var context = await provider.ModelInvokingAsync(messages, CancellationToken.None);

        Assert.Null(context.Instructions);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ModelInvoking_NoMatchingMemories_ReturnsNullInstructions()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            Assert.Equal("/v1/search", PathOf(req.RequestUri!));
            return SearchResponse();
        });
        var provider = CreateProvider("e1", handler);
        var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "anything memorable?") };

        var context = await provider.ModelInvokingAsync(messages, CancellationToken.None);

        Assert.Null(context.Instructions);
    }

    [Fact]
    public async Task ModelInvoking_MinRelevanceFiltersLowerScoredHits()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            Assert.Equal("/v1/search", PathOf(req.RequestUri!));
            return SearchResponse(
                ("r1", 90, "prefers tea over coffee"),
                ("r2", 40, "mentioned a trip to Denver once"));
        });
        var options = new AetherMemoryProviderOptions { MinRelevance = 0.5 };
        var provider = CreateProvider("e1", handler, options);
        var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "what do I drink?") };

        var context = await provider.ModelInvokingAsync(messages, CancellationToken.None);

        Assert.NotNull(context.Instructions);
        Assert.Contains("prefers tea over coffee", context.Instructions);
        Assert.DoesNotContain("mentioned a trip to Denver once", context.Instructions);
    }

    [Fact]
    public async Task ModelInvoking_SearchQueryCarriesEntityIdAndConfiguredK()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            Assert.Equal("/v1/search", PathOf(req.RequestUri!));
            return SearchResponse(("r1", 90, "some remembered detail"));
        });
        var options = new AetherMemoryProviderOptions { K = 3 };
        var provider = CreateProvider("scoped-entity", handler, options);
        var messages = new List<ChatMessage> { new ChatMessage(ChatRole.User, "remind me") };

        await provider.ModelInvokingAsync(messages, CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Contains("entity_id=scoped-entity", req.Uri.Query);
        Assert.Contains("k=3", req.Uri.Query);
    }

    // ── Round trip: the SK-visible contract end to end ──────────────────

    [Fact]
    public async Task RoundTrip_RememberedMessageIsRecalledInInstructions()
    {
        var handler = new RoutingHandler((req, _) =>
        {
            var path = PathOf(req.RequestUri!);
            return path switch
            {
                "/v1/documents" => InsertResponse("e1"),
                "/v1/search" => SearchResponse(("r1", 95, "my favorite color is blue")),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected path {path}"),
            };
        });
        var provider = CreateProvider("e1", handler);

        await provider.MessageAddingAsync(
            null, new ChatMessage(ChatRole.User, "my favorite color is blue"), CancellationToken.None);
        var context = await provider.ModelInvokingAsync(
            new List<ChatMessage> { new ChatMessage(ChatRole.User, "what is my favorite color?") },
            CancellationToken.None);

        Assert.NotNull(context.Instructions);
        Assert.Contains("blue", context.Instructions);
    }

    // ── Construction ─────────────────────────────────────────────────────

    [Fact]
    public void Constructors_NullArguments_ThrowArgumentNullException_ValidProviderExposesEntityId()
    {
        var handler = new RoutingHandler((_, __) => Json(new { }));
        var client = CreateClient(handler);

        Assert.Throws<ArgumentNullException>(() => new AetherMemoryProvider((AetherClient)null!, "e1"));
        Assert.Throws<ArgumentNullException>(() => new AetherMemoryProvider((Memory)null!));

        var provider = new AetherMemoryProvider(client, "e1");
        Assert.Equal("e1", provider.EntityId);
    }
}
