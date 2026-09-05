using RioEditor.Core.Models;

namespace RioEditor.Core.Export;

/// <summary>
/// Produces shareable artefacts from the document. HTML export is pure and platform-neutral, so it
/// lives here; PDF needs a renderer and is a platform capability (see <see cref="IPdfExporter"/>).
/// </summary>
public interface IExportService
{
    /// <summary>
    /// Renders Markdown into a self-contained HTML file: the editor's stylesheet is inlined, so the
    /// export opens anywhere and still looks like what the author saw.
    /// </summary>
    string BuildStandaloneHtml(string markdown, string title, AppTheme theme, bool allowRemoteScripts);
}
