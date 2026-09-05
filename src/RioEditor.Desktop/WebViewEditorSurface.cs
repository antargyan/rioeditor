using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaWebView;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using WebViewCore.Enums;
using WebViewCore.Events;

namespace RioEditor.Desktop;

/// <summary>
/// Desktop editing surface: the native <see cref="WebView"/> control.
/// Backends per platform — WebView2 (Windows), WebKitGTK (Linux), WKWebView (macOS).
/// </summary>
public sealed class WebViewEditorSurface : IEditorSurface, IWebViewTransport
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private WebView? _webView;
    private bool _documentLoaded;
    private bool _loadingOwnDocument;
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
            _webView = new WebView();
        }
        catch (Exception e)
        {
            // Missing WebView2 runtime on Windows, missing libwebkit2gtk on Linux, ...
            Fail(e.Message);
            // The shell reads IsAvailable straight after this returns and swaps in the diagnostic
            // panel, so this placeholder is never actually seen.
            return new Panel();
        }

        _webView.WebMessageReceived += OnWebMessageReceived;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.WebViewCreated += OnWebViewCreated;

        // Every navigation away from the editor document is a link click: open it in the user's
        // real browser instead of replacing the editing surface.
        _webView.NavigationStarting += OnNavigationStarting;
        _webView.WebViewNewWindowRequested += OnNewWindowRequested;

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
        // Tells OnNavigationStarting that the navigation it is about to see is our own document
        // and not a link the user clicked.
        _loadingOwnDocument = true;

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            // HtmlContent feeds the document straight into the native control; no temp files and
            // no local HTTP server, which keeps the whole surface origin-less and CSP-friendly.
            _webView.HtmlContent = html;
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
                await _webView.ExecuteScriptAsync(script);
            }
            catch (Exception)
            {
                // A dead WebView must not propagate into view-model command pipelines.
            }
        });
    }

    public event EventHandler<string>? MessageReceived;

    // ------------------------------------------------------------------ WebView events

    private void OnWebMessageReceived(object? sender, WebViewMessageReceivedEventArgs e)
    {
        // The engine posts JSON strings; MessageAsJson is populated by WebView2, Message by the others.
        var payload = string.IsNullOrEmpty(e.Message) ? e.MessageAsJson : e.Message;
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

    private void OnNavigationCompleted(object? sender, WebViewUrlLoadedEventArg e) => FlushPendingScripts();

    private void OnWebViewCreated(object? sender, WebViewCreatedEventArgs e)
    {
        // This, not an exception from the constructor, is how a missing WebView2 runtime usually
        // announces itself: the Avalonia control builds fine and the *native* backend behind it
        // fails later. Reporting it here is what stops the app showing an empty editor area.
        if (!e.IsSucceed)
        {
            Fail(e.Message);
        }
    }

    private void OnNavigationStarting(object? sender, WebViewUrlLoadingEventArg e)
    {
        var url = e.Url?.ToString();

        // The editor document itself must be allowed through, and the backends disagree about
        // what it looks like: WKWebView and WebKitGTK report about:blank, but WebView2 loads
        // HtmlContent by navigating to a data:text/html URL. Treating only about: as our own
        // document cancelled the document load on Windows, which left a blank editor that never
        // signalled ready — no error, no crash, just nothing.
        //
        // The data: form is additionally gated on _loadingOwnDocument so that only the navigation
        // we just triggered is allowed; a data: URL arriving from anywhere else is still a link.
        if (string.IsNullOrEmpty(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            (_loadingOwnDocument && url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)))
        {
            _loadingOwnDocument = false;
            return;
        }

        e.Cancel = true;
        OpenInSystemBrowser(url);
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowEventArgs e)
    {
        // Keep the editor in place; the link opens outside the app instead.
        e.UrlLoadingStrategy = UrlRequestStrategy.CancelLoad;
        var url = e.Url?.ToString();
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
    /// editor whether the failure arrived synchronously or from the WebViewCreated event.
    /// </summary>
    private void Fail(string? detail)
    {
        if (_failure is not null)
        {
            return;
        }

        _failure = DescribeFailure();
        _failureDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;

        // The event may arrive off the UI thread; the shell touches controls in its handler.
        Dispatcher.UIThread.Post(() => BecameUnavailable?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Written for whoever is looking at the screen, which on Windows is usually someone who has
    /// never heard of WebView2: name the missing thing, say it is free and safe to install, and
    /// give the one address or command that fixes it. The exception text is kept separately in
    /// <see cref="UnavailableDetail"/> rather than mixed into this.
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

        return "RioEditor shows your document using WKWebView, which is part of macOS, so this " +
               "failure is unexpected.\n\n" +
               "Restarting RioEditor may clear it. If it keeps happening, please report the " +
               "details below.";
    }
}
