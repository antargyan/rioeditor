using Avalonia.Controls;
using RioEditor.Core.Editor;

namespace RioEditor.App.Services;

/// <summary>
/// The platform-provided editing surface: a native WebView on desktop, an overlaid iframe on WASM.
/// The shared UI only ever sees a <see cref="Control"/> plus an <see cref="IWebViewTransport"/>.
/// </summary>
public interface IEditorSurface
{
    /// <summary>
    /// False when no WebView is available; the shell then shows a diagnostic panel. Only
    /// meaningful after <see cref="CreateView"/> has been called — a surface cannot know whether
    /// its backend exists until it tries to build it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>What to tell the user, in their language: what is missing and how to fix it.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// The underlying technical error, for a bug report. Shown beneath the reason in smaller type,
    /// never in place of it.
    /// </summary>
    string? UnavailableDetail { get; }

    /// <summary>
    /// Raised when a surface discovers only *after* <see cref="CreateView"/> returned that it
    /// cannot run. Native WebView creation is asynchronous on Windows and Android, so a missing
    /// runtime surfaces here rather than as an exception; without this the shell would leave an
    /// empty editor area and say nothing.
    /// </summary>
    event EventHandler? BecameUnavailable;

    /// <summary>Creates (once) the control that hosts the editor.</summary>
    Control CreateView();

    IWebViewTransport Transport { get; }
}
