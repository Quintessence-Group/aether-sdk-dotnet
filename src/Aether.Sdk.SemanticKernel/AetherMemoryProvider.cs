using System.Text;
using Aether.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Aether.Sdk.SemanticKernel;

/// <summary>
/// A Semantic Kernel <see cref="AIContextProvider"/> that gives an agent durable,
/// entity-scoped memory backed by Aether.
/// </summary>
/// <remarks>
/// <para>
/// Attach it to an agent thread and the agent gains long-term memory that persists
/// across sessions and processes:
/// </para>
/// <list type="bullet">
/// <item>each new chat message is remembered (scoped to the configured entity), and</item>
/// <item>before every model turn the most relevant remembered items for the current
/// request are injected as additional instructions.</item>
/// </list>
/// <para>
/// Memory is scoped to a single entity id (a user, customer, or agent) supplied at
/// construction, so one provider serves one memory owner. Embedding and retrieval
/// happen in the Aether service; nothing is embedded client-side. The provider never
/// disposes the <see cref="Memory"/> or client it is given — ownership stays with the
/// caller.
/// </para>
/// </remarks>
public sealed class AetherMemoryProvider : AIContextProvider
{
    private readonly Memory _memory;
    private readonly AetherMemoryProviderOptions _options;

    /// <summary>
    /// Creates a provider over an existing entity-scoped <see cref="Memory"/>.
    /// </summary>
    /// <param name="memory">The memory to read and write. Not disposed by this provider.</param>
    /// <param name="options">Optional tuning; defaults are used when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/> is null.</exception>
    public AetherMemoryProvider(Memory memory, AetherMemoryProviderOptions? options = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _options = options ?? new AetherMemoryProviderOptions();
    }

    /// <summary>
    /// Creates a provider that scopes an existing <paramref name="client"/> to
    /// <paramref name="entityId"/> (the memory owner). The caller retains ownership
    /// of <paramref name="client"/>; this provider does not dispose it.
    /// </summary>
    /// <param name="client">An already-built Aether client.</param>
    /// <param name="entityId">The entity to scope memory to (1–256 characters).</param>
    /// <param name="options">Optional tuning; defaults are used when omitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is empty or too long.</exception>
    public AetherMemoryProvider(AetherClient client, string entityId, AetherMemoryProviderOptions? options = null)
        : this(new Memory(entityId, client ?? throw new ArgumentNullException(nameof(client))), options)
    {
    }

    /// <summary>The entity id every memory operation is scoped to.</summary>
    public string EntityId => _memory.EntityId;

    /// <summary>
    /// Remembers an incoming message. User messages are stored by default; assistant
    /// messages only when <see cref="AetherMemoryProviderOptions.IncludeAssistantMessages"/>
    /// is set. Empty or whitespace messages, and other roles (system/tool), are ignored.
    /// </summary>
    public override async Task MessageAddingAsync(
        string? conversationId,
        ChatMessage newMessage,
        CancellationToken cancellationToken = default)
    {
        if (newMessage is null || !ShouldRemember(newMessage.Role))
            return;

        var text = newMessage.Text;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var metadata = new Dictionary<string, object?>
        {
            ["role"] = newMessage.Role.Value,
        };

        await _memory
            .RememberAsync(text, metadata, extract: _options.ExtractFacts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recalls the memories most relevant to the incoming user message(s) and returns
    /// them as additional model instructions. Returns an empty <see cref="AIContext"/>
    /// when there is no user text to search on or nothing relevant is found.
    /// </summary>
    public override async Task<AIContext> ModelInvokingAsync(
        ICollection<ChatMessage> newMessages,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(newMessages);
        if (string.IsNullOrWhiteSpace(query))
            return new AIContext();

        var memories = await _memory
            .RecallAsync(query, k: _options.K, recencyWeight: _options.RecencyWeight, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var relevant = _options.MinRelevance is { } min
            ? memories.Where(m => (m.Score ?? 0.0) >= min).ToList()
            : memories;

        if (relevant.Count == 0)
            return new AIContext();

        return new AIContext { Instructions = FormatInstructions(relevant) };
    }

    private bool ShouldRemember(ChatRole role) =>
        role == ChatRole.User || (_options.IncludeAssistantMessages && role == ChatRole.Assistant);

    // Build a retrieval query from the user turns about to be sent to the model.
    private static string BuildQuery(ICollection<ChatMessage> messages)
    {
        if (messages is null || messages.Count == 0)
            return string.Empty;

        var parts = messages
            .Where(m => m is not null && m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Text.Trim());

        return string.Join("\n", parts);
    }

    private string FormatInstructions(IReadOnlyList<MemoryItem> memories)
    {
        var sb = new StringBuilder();
        sb.Append(_options.ContextPrompt);
        foreach (var memory in memories)
        {
            sb.Append("\n- ").Append(memory.Text);
        }

        return sb.ToString();
    }
}
