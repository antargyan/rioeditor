using System.Net;
using System.Text;
using RioEditor.Core.Editor;
using RioEditor.Core.Markdown;
using RioEditor.Core.Models;

namespace RioEditor.Core.Export;

/// <inheritdoc />
public sealed class ExportService : IExportService
{
    private readonly IMarkdownService _markdown;

    public ExportService(IMarkdownService markdown) => _markdown = markdown;

    /// <summary>
    /// Print-oriented overrides layered on top of the editor stylesheet. The editor sheet is built
    /// for an editing surface — a fixed reading column, deep bottom padding for the caret, and
    /// contenteditable affordances — none of which belong in an exported page.
    /// </summary>
    private const string ExportCss = """
        /* ---- export overrides ---- */
        #page { padding: 32px 24px; display: block; }
        #editor { margin: 0 auto; }

        @media print {
          @page { margin: 18mm 16mm; }
          html, body { background: #ffffff !important; color: #000000 !important; }
          #page { padding: 0; }
          #editor { max-width: none; }

          /* Keep structures intact across page breaks. */
          pre, blockquote, table, figure, .mermaid { break-inside: avoid; page-break-inside: avoid; }
          h1, h2, h3, h4, h5, h6 { break-after: avoid; page-break-after: avoid; }
          img { max-width: 100% !important; }

          /* A printed link is useless unless its target is visible. */
          a { color: inherit; border-bottom: none; }
          a[href^="http"]::after { content: " (" attr(href) ")"; font-size: 0.85em; opacity: 0.7; }
        }
        """;

    public string BuildStandaloneHtml(string markdown, string title, AppTheme theme,
        bool allowRemoteScripts)
    {
        var body = _markdown.ToHtml(markdown);

        var builder = new StringBuilder(body.Length + EditorDocumentFactory.EditorCss.Length + 2048);
        builder.Append("<!DOCTYPE html>\n<html lang=\"en\" data-theme=\"")
               .Append(theme == AppTheme.Dark ? "dark" : "light")
               .Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
               .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
               .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n")
               .Append("<meta name=\"generator\" content=\"RioEditor\">\n");

        if (allowRemoteScripts)
        {
            builder.Append(EditorDocumentFactory.RemoteScripts).Append('\n');
        }

        builder.Append("<style>\n")
               .Append(EditorDocumentFactory.EditorCss).Append('\n')
               .Append(ExportCss)
               .Append("\n</style>\n</head>\n<body>\n")
               .Append("<main id=\"page\">\n<div id=\"editor\">\n")
               .Append(body)
               .Append("\n</div>\n</main>\n");

        // The same highlighter the editor uses, so an export looks like the document it came from.
        builder.Append("<script>\n").Append(EditorDocumentFactory.HighlightJs).Append("\n</script>\n");

        builder.Append("""
            <script>
              window.addEventListener('load', function () {
                if (window.rioHighlight) window.rioHighlight.applyAll(document);

                if (typeof window.renderMathInElement === 'function') {
                  window.renderMathInElement(document.body, {
                    // Markdig's Mathematics extension emits \( \) and \[ \], not just $ and $$.
                    // Omitting those leaves inline math rendered as literal backslash-paren text.
                    delimiters: [
                      { left: '$$', right: '$$', display: true },
                      { left: '$', right: '$', display: false },
                      { left: '\\(', right: '\\)', display: false },
                      { left: '\\[', right: '\\]', display: true }
                    ],
                    throwOnError: false,
                    ignoredTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code'],
                    ignoredClasses: ['katex', 'katex-display']
                  });
                }
                if (window.mermaid) {
                  window.mermaid.initialize({ startOnLoad: true, securityLevel: 'strict' });
                }
              });
            </script>

            """);

        builder.Append("</body>\n</html>\n");
        return builder.ToString();
    }
}
