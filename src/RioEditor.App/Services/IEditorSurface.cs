using Avalonia.Controls;
using RioEditor.Core.Editor;

namespace RioEditor.App.Services;

/// <summary>
/// The platform-provided editing surface: a native WebView on desktop, an overlaid iframe on WASM.
/// The shared UI only ever sees a <see cref="Control"/> plus an <see cref="IWebViewTransport"/>.
/// </summary>
public interface IEditorSurface
{
    /// <summary>False when no WebView is available; the shell then shows a diagnostic panel.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>Creates (once) the control that hosts the editor.</summary>
    Control CreateView();

    IWebViewTransport Transport { get; }
}
