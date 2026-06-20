using Xunit;

namespace Aether.Sdk.Tests;

public class FormatContextDefaultsTests
{
    private static RetrievalResult MakeResult(
        string docId = "d1",
        int score = 90,
        string content = "full body",
        string? passage = null,
        string? title = null) =>
        new()
        {
            DocId = docId,
            Score = score,
            Content = content,
            Passage = passage,
            Title = title,
        };

    [Fact]
    public void EmptyReturnsEmptyString()
    {
        Assert.Equal("", new List<RetrievalResult>().FormatContext());
    }

    [Fact]
    public void DefaultTemplateNumbersSourcesFromOne()
    {
        var results = new[] { MakeResult(docId: "a"), MakeResult(docId: "b") };
        var output = results.FormatContext();
        Assert.Contains("[Source 1]", output);
        Assert.Contains("[Source 2]", output);
        Assert.DoesNotContain("[Source 0]", output);
    }

    [Fact]
    public void DefaultSeparatorIsBlankLine()
    {
        var results = new[]
        {
            MakeResult(docId: "a", content: "alpha"),
            MakeResult(docId: "b", content: "beta"),
        };
        Assert.Equal("[Source 1]\nalpha\n\n[Source 2]\nbeta", results.FormatContext());
    }

    [Fact]
    public void PrefersPassageOverContentByDefault()
    {
        // Long-form docs: passage is the matched chunk; content is the whole doc.
        var results = new[]
        {
            MakeResult(content: "100-page handbook", passage: "the matched paragraph"),
        };
        var output = results.FormatContext();
        Assert.Contains("the matched paragraph", output);
        Assert.DoesNotContain("100-page handbook", output);
    }

    [Fact]
    public void FallsBackToContentWhenPassageMissing()
    {
        // Short single-chunk inserts (the quickstart shape) have no separate passage.
        var results = new[] { MakeResult(content: "short doc") };
        Assert.Contains("short doc", results.FormatContext());
    }
}

public class FormatContextOptionsTests
{
    private static RetrievalResult MakeResult(
        string docId = "d1",
        int score = 90,
        string content = "full body",
        string? passage = null,
        string? title = null) =>
        new()
        {
            DocId = docId,
            Score = score,
            Content = content,
            Passage = passage,
            Title = title,
        };

    [Fact]
    public void PreferPassageFalseUsesContent()
    {
        var results = new[] { MakeResult(content: "full body", passage: "chunk") };
        var output = results.FormatContext(preferPassage: false);
        Assert.Contains("full body", output);
        Assert.DoesNotContain("chunk", output);
    }

    [Fact]
    public void CustomTemplateWithTitleAndScore()
    {
        var results = new[]
        {
            MakeResult(docId: "d1", title: "PTO policy", score: 92, content: "20 days"),
        };
        var output = results.FormatContext(template: "<{title} | s={score:.1f}>\n{text}");
        Assert.Equal("<PTO policy | s=92.0>\n20 days", output);
    }

    [Fact]
    public void CustomTemplateFallsBackToDocIdWhenTitleMissing()
    {
        var results = new[] { MakeResult(docId: "d1", title: null, content: "body") };
        var output = results.FormatContext(template: "[{title}] {text}");
        Assert.Equal("[d1] body", output);
    }

    [Fact]
    public void CustomSeparator()
    {
        var results = new[] { MakeResult(content: "a"), MakeResult(content: "b") };
        var output = results.FormatContext(separator: " --- ");
        Assert.Contains(" --- ", output);
        Assert.DoesNotContain("\n\n", output);
    }
}

public class FormatContextResultTypesTests
{
    [Fact]
    public void RetrievalResultWithoutPassageRendersContent()
    {
        // Retrieve always populates Content; Passage may be null for short single-chunk inserts.
        var results = new[]
        {
            new RetrievalResult { DocId = "d1", Score = 90, Content = "search body" },
        };
        Assert.Contains("search body", results.FormatContext());
    }

    [Fact]
    public void RetrievalResultWithBlankContentAndNoPassageRendersEmptyText()
    {
        var results = new[]
        {
            new RetrievalResult { DocId = "d1", Score = 90, Content = "" },
        };
        var output = results.FormatContext();
        Assert.Contains("[Source 1]\n", output);
        Assert.EndsWith("\n", output);
    }
}
