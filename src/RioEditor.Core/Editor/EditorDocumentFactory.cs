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

    public static string Build(AppTheme theme, bool allowRemoteScripts) =>
        Template.Value
            .Replace("/*{{STYLES}}*/", Styles.Value)
            .Replace("/*{{ENGINE}}*/", Engine.Value)
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
