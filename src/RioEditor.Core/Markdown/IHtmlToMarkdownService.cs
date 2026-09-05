namespace RioEditor.Core.Markdown;

/// <summary>
/// HTML -> Markdown half of the live pipeline (the "reverse" direction).
/// This is the canonical extractor: what the user sees in the contenteditable is HTML, and this
/// is what turns it back into the Markdown we write to disk.
/// </summary>
public interface IHtmlToMarkdownService
{
    string ToMarkdown(string html);
}
