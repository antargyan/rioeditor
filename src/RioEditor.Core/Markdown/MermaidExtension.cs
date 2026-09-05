using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace RioEditor.Core.Markdown;

/// <summary>
/// Turns <c>```mermaid</c> fenced blocks into <c>&lt;div class="mermaid"&gt;</c> so the browser-side
/// Mermaid runtime can pick them up. Every other fenced block falls through to Markdig's own renderer.
/// </summary>
public sealed class MermaidExtension : IMarkdownExtension
{
    public const string InfoPrefix = "mermaid";

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // Nothing to add to the parser: mermaid blocks are ordinary fenced code blocks.
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer htmlRenderer)
        {
            return;
        }

        var existing = htmlRenderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (existing is not null)
        {
            htmlRenderer.ObjectRenderers.Remove(existing);
        }

        htmlRenderer.ObjectRenderers.AddIfNotAlready(
            new MermaidCodeBlockRenderer(existing ?? new CodeBlockRenderer()));
    }
}

/// <summary>Decorator around the stock <see cref="CodeBlockRenderer"/>.</summary>
internal sealed class MermaidCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    private readonly CodeBlockRenderer _inner;

    public MermaidCodeBlockRenderer(CodeBlockRenderer inner) => _inner = inner;

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        if (obj is FencedCodeBlock fenced &&
            string.Equals(fenced.Info?.Trim(), MermaidExtension.InfoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            renderer.EnsureLine();
            // The raw graph definition is escaped: Mermaid reads textContent, so escaping is safe
            // and it keeps the block harmless if the Mermaid runtime never loads.
            renderer.Write("<div class=\"mermaid\" data-rio-block=\"mermaid\">");
            var lines = fenced.Lines.Lines;
            for (var i = 0; i < fenced.Lines.Count; i++)
            {
                renderer.WriteEscape(lines[i].Slice.ToString());
                renderer.Write('\n');
            }

            renderer.Write("</div>");
            renderer.EnsureLine();
            return;
        }

        _inner.Write(renderer, obj);
    }
}
