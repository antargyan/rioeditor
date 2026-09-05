using System.Text;
using Markdig;
using Markdig.Syntax;
using RioEditor.Core.Sanitization;

namespace RioEditor.Core.Markdown;

/// <summary>
/// Markdig-backed renderer. The pipeline is built once and reused; Markdig pipelines are
/// immutable and thread-safe once constructed.
/// </summary>
public sealed class MarkdownService : IMarkdownService
{
    private readonly IHtmlSanitizer _sanitizer;
    private readonly MarkdownPipeline _pipeline;

    public MarkdownService(IHtmlSanitizer sanitizer)
    {
        _sanitizer = sanitizer;
        _pipeline = BuildPipeline();
    }

    public static MarkdownPipeline BuildPipeline() =>
        new MarkdownPipelineBuilder()
            // Tables (grid + pipe), emphasis extras, definition lists, abbreviations,
            // figures, footers, citations, custom containers, attributes, list extras.
            .UseAdvancedExtensions()
            .UsePipeTables()
            .UseGridTables()
            .UseTaskLists()
            .UseFootnotes()
            .UseAutoLinks()
            .UseEmojiAndSmiley()
            .UseYamlFrontMatter()
            // Math -> <span class="math"> / <div class="math">, picked up by KaTeX auto-render.
            .UseMathematics()
            .UseSoftlineBreakAsHardlineBreak()
            // ```mermaid -> <div class="mermaid">
            .Use<MermaidExtension>()
            .Build();

    public string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var html = Markdig.Markdown.ToHtml(markdown, _pipeline);
        return _sanitizer.Sanitize(html);
    }

    public string ToHtmlBlock(string markdown)
    {
        // A single block still goes through the full pipeline; the win is that the caller only
        // replaces one node in the DOM instead of the whole document.
        var html = ToHtml(markdown);
        return string.IsNullOrWhiteSpace(html) ? "<p><br></p>" : html;
    }

    public string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var document = Markdig.Markdown.Parse(markdown, _pipeline);
        var builder = new StringBuilder();
        foreach (var node in document.Descendants())
        {
            if (node is Markdig.Syntax.Inlines.LiteralInline literal)
            {
                builder.Append(literal.Content.ToString());
            }
            else if (node is Markdig.Syntax.Inlines.LineBreakInline)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
