namespace RioEditor.Core.Editor;

/// <summary>
/// The only platform-specific piece of the editor. Desktop implements it over
/// <c>AvaloniaWebView</c>; the WASM head implements it over a JS-interop channel to an iframe.
/// </summary>
public interface IWebViewTransport
{
    /// <summary>Loads the editor document into the surface.</summary>
    Task LoadHtmlAsync(string html, CancellationToken cancellationToken = default);

    /// <summary>Evaluates JavaScript in the editor document. Fire-and-forget by design.</summary>
    Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>Raised for every message the editor engine posts back to the host.</summary>
    event EventHandler<string>? MessageReceived;
}
