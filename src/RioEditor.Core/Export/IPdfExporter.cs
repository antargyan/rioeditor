namespace RioEditor.Core.Export;

/// <summary>
/// Optional capability implemented by an editing surface that can turn the rendered document into
/// a PDF. Implemented by the surface rather than by a shared service on purpose: the only component
/// that already knows how to lay this document out is the WebView showing it, so using its own
/// renderer is what makes the PDF match the screen.
/// </summary>
public interface IPdfExporter
{
    /// <summary>
    /// True when the platform can hand back PDF bytes directly (WKWebView). False means the best
    /// available route is the system print UI, where the user chooses "Save as PDF" themselves.
    /// </summary>
    bool CanProducePdfBytes { get; }

    /// <summary>Renders the document to PDF. Null when the platform cannot, or on failure.</summary>
    Task<byte[]?> ExportPdfBytesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the platform print UI. Returns false when the platform has none, in which case the
    /// caller falls back to <c>window.print()</c> inside the document.
    /// </summary>
    Task<bool> TryShowPrintUiAsync();
}
