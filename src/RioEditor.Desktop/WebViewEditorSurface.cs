using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using RioEditor.App.Services;
using RioEditor.Core.Editor;

namespace RioEditor.Desktop;

/// <summary>
/// Desktop editing surface: Avalonia's own <see cref="NativeWebView"/>.
/// Backends per platform — WebView2 (Windows), WebKitGTK (Linux).
/// </summary>
public sealed class WebViewEditorSurface : IEditorSurface, IWebViewTransport
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private NativeWebView? _webView;
    private bool _documentLoaded;
    private string? _failure;
    private string? _failureDetail;

    public bool IsAvailable => _failure is null;

    public string? UnavailableReason => _failure;

    public string? UnavailableDetail => _failureDetail;

    public event EventHandler? BecameUnavailable;

    public IWebViewTransport Transport => this;

    public Control CreateView()
    {
        if (_webView is not null)
        {
            return _webView;
        }

        try
        {
            // No builder call and no AppBuilder extension: unlike the previous library, this
            // control needs no initialisation before it can be constructed.
            _webView = new NativeWebView();
        }
        catch (Exception e)
        {
            Fail(e.Message);
            // The shell reads IsAvailable straight after this returns and swaps in the diagnostic
            // panel, so this placeholder is never actually seen.
            return new Panel();
        }

        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += OnNavigationCompleted;

        // Every navigation away from the editor document is a link click: open it in the user's
        // real browser instead of replacing the editing surface.
        _webView.NavigationStarted += OnNavigationStarting;
        _webView.NewWindowRequested += OnNewWindowRequested;

        return _webView;
    }

    // ------------------------------------------------------------------ IWebViewTransport

    public Task LoadHtmlAsync(string html, CancellationToken cancellationToken = default)
    {
        if (_webView is null)
        {
            return Task.CompletedTask;
        }

        _documentLoaded = false;
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Feeds the document straight into the native control; no temp files and no local
            // HTTP server, which keeps the whole surface origin-less and CSP-friendly.
            _webView.NavigateToString(html);
        }).GetTask();
    }

    public Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        if (_webView is null)
        {
            return Task.CompletedTask;
        }

        // Scripts issued before the document finished loading are replayed in order afterwards.
        if (!_documentLoaded)
        {
            _pendingScripts.Enqueue(script);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await _webView.InvokeScript(script);
            }
            catch (Exception)
            {
                // A dead WebView must not propagate into view-model command pipelines.
            }
        });
    }

    public event EventHandler<string>? MessageReceived;

    // ------------------------------------------------------------------ WebView events

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var payload = e.Body;
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        // WebView2 wraps a posted string in JSON quotes; unwrap when that happened.
        if (payload.Length > 1 && payload[0] == '"' && payload[^1] == '"')
        {
            try
            {
                payload = System.Text.Json.JsonSerializer.Deserialize<string>(payload) ?? payload;
            }
            catch (System.Text.Json.JsonException)
            {
                // Leave it as-is; the bridge will reject anything it cannot parse.
            }
        }

        MessageReceived?.Invoke(this, payload);
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) =>
        FlushPendingScripts();

    private void OnNavigationStarting(object? sender, WebViewNavigationStartingEventArgs e)
    {
        var url = e.Request?.ToString();

        // The editor document itself must be allowed through. NavigateToString lands on about:blank
        // here rather than the data: URL the previous library used, but both shapes are treated as
        // ours: cancelling the document load leaves a blank editor that never signals ready.
        if (string.IsNullOrEmpty(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(url);
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        // Keep the editor in place; the link opens outside the app instead.
        e.Handled = true;
        var url = e.Request?.ToString();
        if (!string.IsNullOrEmpty(url))
        {
            OpenInSystemBrowser(url);
        }
    }

    private void FlushPendingScripts()
    {
        _documentLoaded = true;
        while (_pendingScripts.TryDequeue(out var script))
        {
            _ = ExecuteScriptAsync(script);
        }
    }

    private static void OpenInSystemBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // No browser, no problem — the click is simply ignored.
        }
    }

    /// <summary>
    /// Records a backend failure once and tells the shell, so the diagnostic panel can replace the
    /// editor whether the failure arrived synchronously or later.
    /// </summary>
    private void Fail(string? detail)
    {
        if (_failure is not null)
        {
            return;
        }

        _failure = DescribeFailure();
        _failureDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;

        Dispatcher.UIThread.Post(() => BecameUnavailable?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Written for whoever is looking at the screen, which on Windows is usually someone who has
    /// never heard of WebView2: name the missing thing, say it is free and safe to install, and
    /// give the one address or command that fixes it.
    /// </summary>
    private static string DescribeFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return "RioEditor shows your document using the Microsoft Edge WebView2 Runtime, and " +
                   "it is not installed on this computer.\n\n" +
                   "It is a free Microsoft component. Install it, then start RioEditor again:\n" +
                   "https://developer.microsoft.com/microsoft-edge/webview2/";
        }

        if (OperatingSystem.IsLinux())
        {
            return "RioEditor shows your document using WebKitGTK, and it is not installed on " +
                   "this computer.\n\n" +
                   "Install it, then start RioEditor again:\n" +
                   "sudo apt install libwebkit2gtk-4.1-0\n\n" +
                   "On older distributions the package is named libwebkit2gtk-4.0-37.";
        }

        return "RioEditor could not start its editing surface, which is unexpected on this " +
               "platform.\n\nRestarting RioEditor may clear it. If it keeps happening, please " +
               "report the details below.";
    }
}
