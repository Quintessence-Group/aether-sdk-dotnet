namespace Aether.Sdk.SemanticKernel;

/// <summary>
/// Tuning options for <see cref="AetherMemoryProvider"/>. Every value has a
/// sensible default, so <c>new AetherMemoryProviderOptions()</c> is a valid
/// configuration.
/// </summary>
public sealed class AetherMemoryProviderOptions
{
    /// <summary>
    /// Maximum number of remembered items injected into the model context before a
    /// turn. Default 5.
    /// </summary>
    public int K { get; set; } = 5;

    /// <summary>
    /// Blend weight in <c>[0, 1]</c> (clamped) that trades pure relevance
    /// (<c>0</c>, the default) for recency. Values greater than <c>0</c> ask the
    /// recall to favor more recent memories at some extra lookup cost.
    /// </summary>
    public double RecencyWeight { get; set; }

    /// <summary>
    /// Optional minimum relevance in <c>[0, 1]</c> (higher = stricter). Recalled
    /// items scoring below this are dropped before injection. <c>null</c> (default)
    /// keeps all <see cref="K"/> results regardless of score.
    /// </summary>
    public double? MinRelevance { get; set; }

    /// <summary>
    /// Preamble placed before the injected memories in the model context. Kept
    /// short because it is added on every turn that recalls at least one memory.
    /// </summary>
    public string ContextPrompt { get; set; } =
        "## Remembered context\nThe following was remembered about the user from earlier sessions. Use it when relevant:";

    /// <summary>
    /// When <c>true</c>, remembered messages are distilled into atomic facts
    /// server-side (requires fact extraction to be configured for the account).
    /// Default <c>false</c>.
    /// </summary>
    public bool ExtractFacts { get; set; }

    /// <summary>
    /// When <c>true</c>, assistant messages are remembered as well, not just user
    /// messages. Default <c>false</c> — user turns carry the durable signal, while
    /// assistant turns are often redundant or transient.
    /// </summary>
    public bool IncludeAssistantMessages { get; set; }
}
