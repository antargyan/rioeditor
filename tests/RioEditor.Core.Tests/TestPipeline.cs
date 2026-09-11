using RioEditor.Core.Export;
using RioEditor.Core.Markdown;
using RioEditor.Core.Sanitization;

namespace RioEditor.Core.Tests;

/// <summary>
/// The services under test wired the way the app wires them, so a test exercises the real
/// composition rather than a convenient approximation of it.
/// </summary>
internal static class TestPipeline
{
    public static IHtmlSanitizer Sanitizer { get; } = new HtmlSanitizerService();

    public static IMarkdownService Markdown { get; } = new MarkdownService(Sanitizer);

    public static IHtmlToMarkdownService Reverse { get; } = new HtmlToMarkdownService();

    public static IExportService Export { get; } = new ExportService(Markdown);

    /// <summary>Markdown -> HTML -> Markdown, the path a document takes on every edit.</summary>
    public static string RoundTrip(string markdown) => Reverse.ToMarkdown(Markdown.ToHtml(markdown));

    /// <summary>
    /// Comparison that ignores trailing whitespace and blank-line count, which the pipeline is
    /// entitled to normalise. Everything else must survive untouched.
    /// </summary>
    public static string Normalise(string markdown) =>
        string.Join('\n', markdown.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0));
}
