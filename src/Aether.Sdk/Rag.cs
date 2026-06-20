using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Aether.Sdk;

/// <summary>
/// RAG-specific helpers for the Aether SDK.
///
/// These utilities sit on top of <see cref="AetherClient.RetrieveAsync"/> to
/// remove the last-mile boilerplate of building an LLM prompt context:
/// numbering sources, choosing between the matched passage and the full
/// document content, and joining them into a single string.
/// </summary>
public static class RagExtensions
{
    /// <summary>Default per-source template used by <see cref="FormatContext"/>.</summary>
    public const string DefaultTemplate = "[Source {i}]\n{text}";

    /// <summary>Default string joined between formatted sources.</summary>
    public const string DefaultSeparator = "\n\n";

    private static readonly Regex PlaceholderRe = new(
        @"\{(\w+)(?::([^}]+))?\}", RegexOptions.Compiled);

    /// <summary>
    /// Format retrieve/search results into an LLM-ready context string.
    ///
    /// <para>The default output looks like:</para>
    /// <code>
    /// [Source 1]
    /// &lt;matched passage 1&gt;
    ///
    /// [Source 2]
    /// &lt;matched passage 2&gt;
    /// </code>
    ///
    /// <para>Available template placeholders: <c>{i}</c> (1-based source
    /// number), <c>{doc_id}</c>, <c>{title}</c> (falls back to <c>{doc_id}</c>
    /// when missing), <c>{text}</c> (passage or content), <c>{score}</c>.
    /// Numeric placeholders accept a Python-style fixed-precision spec, e.g.
    /// <c>{score:.1f}</c>.</para>
    /// </summary>
    /// <param name="results">Retrieval results to format.</param>
    /// <param name="template">Per-source format string. Defaults to
    ///     <see cref="DefaultTemplate"/>.</param>
    /// <param name="separator">String joined between formatted sources.
    ///     Defaults to <see cref="DefaultSeparator"/>.</param>
    /// <param name="preferPassage">When true (default), use the matched
    ///     passage if present and fall back to content. When false, use
    ///     content if present and fall back to passage. Passages are the
    ///     right choice for chunked long-form documents; content is fine for
    ///     short single-chunk inserts.</param>
    /// <returns>A single string ready to drop into an LLM system prompt.</returns>
    public static string FormatContext(
        this IEnumerable<RetrievalResult> results,
        string? template = null,
        string? separator = null,
        bool preferPassage = true)
    {
        var tpl = template ?? DefaultTemplate;
        var sep = separator ?? DefaultSeparator;

        var chunks = new List<string>();
        var i = 1;
        foreach (var r in results)
        {
            var passage = r.Passage ?? "";
            var content = r.Content ?? "";
            var text = preferPassage
                ? (passage.Length > 0 ? passage : content)
                : (content.Length > 0 ? content : passage);
            var title = !string.IsNullOrEmpty(r.Title) ? r.Title! : r.DocId;
            chunks.Add(RenderTemplate(tpl, new Dictionary<string, object>
            {
                ["i"] = i,
                ["doc_id"] = r.DocId,
                ["title"] = title,
                ["text"] = text,
                ["score"] = (double)r.Score,
            }));
            i++;
        }
        return string.Join(sep, chunks);
    }

    /// <summary>
    /// Substitute <c>{name}</c> and <c>{name:spec}</c> placeholders from
    /// <paramref name="values"/>. Supports a tiny subset of Python's format
    /// spec — a fixed decimal precision (e.g. <c>.2f</c>) for numbers.
    /// Unknown placeholders are left untouched so a template typo doesn't
    /// silently drop a label.
    /// </summary>
    private static string RenderTemplate(string template, IReadOnlyDictionary<string, object> values)
    {
        return PlaceholderRe.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var spec = match.Groups[2].Success ? match.Groups[2].Value : null;
            if (!values.TryGetValue(name, out var raw)) return match.Value;
            if (spec != null && raw is double d)
            {
                if (spec.Length >= 2 && spec[0] == '.' && spec[spec.Length - 1] == 'f'
                    && int.TryParse(spec.Substring(1, spec.Length - 2),
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return d.ToString("F" + n.ToString(CultureInfo.InvariantCulture),
                                      CultureInfo.InvariantCulture);
                }
            }
            return raw switch
            {
                int iv => iv.ToString(CultureInfo.InvariantCulture),
                double dv => dv.ToString("R", CultureInfo.InvariantCulture),
                _ => raw.ToString() ?? "",
            };
        });
    }
}
