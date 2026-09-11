using Xunit;

namespace RioEditor.Core.Tests;

/// <summary>
/// The editor's document is HTML; the file on disk is Markdown. Every keystroke crosses that
/// boundary, so a defect here does not merely render something oddly — it rewrites the user's
/// file. These tests exist because exactly that happened: a rendered Mermaid diagram was read
/// back as its own SVG and saved over the graph source.
/// </summary>
public class RoundTripTests
{
    [Theory]
    [InlineData("# Heading one")]
    [InlineData("## Heading two")]
    [InlineData("Plain paragraph text.")]
    [InlineData("Text with **bold** inside.")]
    [InlineData("Text with *italic* inside.")]
    [InlineData("Text with `inline code` inside.")]
    [InlineData("Text with ~~strikethrough~~ inside.")]
    [InlineData("A [link](https://example.com) in a sentence.")]
    [InlineData("An ![image](https://example.com/a.png) in a sentence.")]
    [InlineData("- first\n- second\n- third")]
    [InlineData("1. first\n2. second")]
    [InlineData("> quoted line")]
    [InlineData("---")]
    public void Survives_a_round_trip(string markdown)
    {
        var actual = TestPipeline.RoundTrip(markdown);
        Assert.Equal(TestPipeline.Normalise(markdown), TestPipeline.Normalise(actual));
    }

    [Fact]
    public void Round_trip_is_idempotent()
    {
        // A second pass must change nothing. If it does, every autosave mutates the file a little
        // more, and the damage is only visible long after the edit that caused it.
        const string source = """
            # Title

            Body with **bold**, *italic* and `code`.

            - one
            - two

            > quote
            """;

        var once = TestPipeline.RoundTrip(source);
        var twice = TestPipeline.RoundTrip(once);

        Assert.Equal(TestPipeline.Normalise(once), TestPipeline.Normalise(twice));
    }

    [Fact]
    public void Fenced_code_keeps_its_language_and_body()
    {
        const string source = "```csharp\npublic int Add(int a, int b) => a + b;\n```";

        var actual = TestPipeline.RoundTrip(source);

        Assert.Contains("```csharp", actual);
        Assert.Contains("public int Add(int a, int b) => a + b;", actual);
    }

    [Fact]
    public void Code_block_content_is_not_interpreted_as_markdown()
    {
        // Markdown inside a code fence is literal. Losing this corrupts exactly the documents a
        // developer is most likely to be writing.
        const string source = "```\n# not a heading\n**not bold**\n```";

        var actual = TestPipeline.RoundTrip(source);

        Assert.Contains("# not a heading", actual);
        Assert.Contains("**not bold**", actual);
    }

    [Fact]
    public void Task_list_state_survives()
    {
        const string source = "- [x] done\n- [ ] not done";

        var actual = TestPipeline.RoundTrip(source);

        Assert.Contains("[x] done", actual);
        Assert.Contains("[ ] not done", actual);
    }

    [Fact]
    public void Table_survives_with_its_cells()
    {
        const string source = """
            | Metric | Before | After |
            | --- | --- | --- |
            | Completion | 41% | 68% |
            """;

        var actual = TestPipeline.RoundTrip(source);

        Assert.Contains("Metric", actual);
        Assert.Contains("Completion", actual);
        Assert.Contains("41%", actual);
        Assert.Contains("68%", actual);
    }

    [Fact]
    public void Mermaid_source_survives_and_is_not_replaced_by_rendered_output()
    {
        const string source = "```mermaid\ngraph LR\n  A --> B\n```";

        var actual = TestPipeline.RoundTrip(source);

        Assert.Contains("```mermaid", actual);
        Assert.Contains("graph LR", actual);
        Assert.Contains("A --> B", actual);
        Assert.DoesNotContain("<svg", actual);
    }

    [Fact]
    public void Mermaid_source_is_recovered_from_the_stash_after_rendering()
    {
        // This is the regression test for the bug that prompted the suite. Once Mermaid draws the
        // diagram, the element's text is an SVG; the engine stashes the graph in data-rio-source,
        // and the reverse pipeline must prefer the stash over what is on screen.
        const string renderedHtml =
            """<div class="mermaid" data-rio-source="graph LR&#10;  A --> B" data-processed="true"><svg id="x"><g>rendered</g></svg></div>""";

        var markdown = TestPipeline.Reverse.ToMarkdown(renderedHtml);

        Assert.Contains("graph LR", markdown);
        Assert.Contains("A --> B", markdown);
        Assert.DoesNotContain("svg", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rendered", markdown);
    }

    [Fact]
    public void Inline_code_containing_backticks_is_fenced_wide_enough()
    {
        const string html = "<p>Use <code>a ` b</code> here.</p>";

        var markdown = TestPipeline.Reverse.ToMarkdown(html);

        // A single backtick delimiter would terminate at the embedded one and corrupt the line.
        Assert.Contains("``a ` b``", markdown);
    }

    [Fact]
    public void Nested_lists_keep_their_nesting()
    {
        const string source = "- outer\n  - inner";

        var actual = TestPipeline.RoundTrip(source);
        var lines = actual.Split('\n').Where(l => l.Trim().Length > 0).ToArray();

        Assert.Contains(lines, l => l.StartsWith("- outer"));
        Assert.Contains(lines, l => l.StartsWith("  ") && l.Contains("inner"));
    }

    [Fact]
    public void Empty_document_stays_empty()
    {
        Assert.True(string.IsNullOrWhiteSpace(TestPipeline.RoundTrip(string.Empty)));
    }
}
