namespace RioEditor.Core.Markdown;

/// <summary>Markdown -> HTML half of the live pipeline.</summary>
public interface IMarkdownService
{
    /// <summary>Renders a whole document to HTML (already sanitized by the pipeline caller).</summary>
    string ToHtml(string markdown);

    /// <summary>
    /// Renders a single block. Used by the incremental path: when the caret leaves a block we
    /// re-render only that block, which is what keeps typing cheap and the caret stable.
    /// </summary>
    string ToHtmlBlock(string markdown);

    /// <summary>Strips formatting; used for word counts and for plain-text drag/drop.</summary>
    string ToPlainText(string markdown);
}
