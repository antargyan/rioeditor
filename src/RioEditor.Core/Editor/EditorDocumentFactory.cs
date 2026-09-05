using System.Reflection;
using System.Resources;
using System.Text;
using RioEditor.Core.Models;

namespace RioEditor.Core.Editor;

/// <summary>
/// Builds the single-file editor document (HTML + CSS + JS inlined) that gets loaded into the
/// WebView. Inlining everything avoids per-platform asset URL schemes, which differ wildly between
/// WebView2, WKWebView and WebKitGTK — and keeps the whole surface usable with a strict CSP.
/// </summary>
public static class EditorDocumentFactory
{
    private const string ResourcePrefix = "RioEditor.Core.Assets.";

    private static readonly Lazy<string> Template = new(() => ReadResource("editor.html"));
    private static readonly Lazy<string> Styles = new(() => ReadResource("editor.css"));
    private static readonly Lazy<string> Engine = new(() => ReadResource("editor.js"));
    private static readonly Lazy<string> Highlighter = new(() => ReadResource("highlight.js"));

    /// <summary>CDN tags for Mermaid + KaTeX. Omitted entirely when remote scripts are disabled.</summary>
    private const string RemoteScriptTags = """
        <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css">
        <script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js"></script>
        <script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/contrib/auto-render.min.js"></script>
        <script type="module">
          import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs';
          window.mermaid = mermaid;
          window.dispatchEvent(new Event('rio-mermaid-ready'));
        </script>
        """;

    /// <summary>The editor stylesheet, reused by HTML export so exports look like the editor.</summary>
    public static string EditorCss => Styles.Value;

    /// <summary>The CDN tags, reused by HTML export so exported math and diagrams still render.</summary>
    public static string RemoteScripts => RemoteScriptTags;

    /// <summary>The shared code highlighter, inlined by both the editor and HTML export.</summary>
    public static string HighlightJs => Highlighter.Value;

    public static string Build(AppTheme theme, bool allowRemoteScripts) =>
        Template.Value
            .Replace("/*{{STYLES}}*/", Styles.Value)
            // The highlighter must be defined before the engine calls into it.
            .Replace("/*{{ENGINE}}*/", Highlighter.Value + "\n" + Engine.Value)
            .Replace("{{REMOTE_SCRIPTS}}", allowRemoteScripts ? RemoteScriptTags : string.Empty)
            .Replace("{{THEME}}", theme == AppTheme.Dark ? "dark" : "light");

    private static string ReadResource(string name)
    {
        var assembly = typeof(EditorDocumentFactory).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new MissingManifestResourceException(
                $"Embedded editor asset '{name}' was not found. Check the <EmbeddedResource> items in RioEditor.Core.csproj.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
