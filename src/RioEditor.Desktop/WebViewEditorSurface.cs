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
    private string? _failure;

    public bool IsAvailable => _failure is null;

    public string? UnavailableReason => _failure;

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
            _failure = DescribeFailure(e);
            return new TextBlock { Text = _failure, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
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
        if (!e.IsSucceed)
        {
            _failure = string.IsNullOrEmpty(e.Message)
                ? "The native WebView could not be created."
                : e.Message;
        }
    }

    private void OnNavigationStarting(object? sender, WebViewUrlLoadingEventArg e)
    {
        // about:blank and the in-memory document itself must be allowed through.
        var url = e.Url?.ToString();
        if (string.IsNullOrEmpty(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
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

    private static string DescribeFailure(Exception e)
    {
        var platform = OperatingSystem.IsWindows()
            ? "Install the Microsoft Edge WebView2 Runtime (https://developer.microsoft.com/microsoft-edge/webview2/)."
            : OperatingSystem.IsLinux()
                ? "Install WebKitGTK: sudo apt install libwebkit2gtk-4.1-0 (or libwebkit2gtk-4.0-37 on older distributions)."
                : "macOS uses WKWebView, which ships with the OS. If this persists the WebView backend is not compatible with this runtime.";

        return $"The editing surface needs a system WebView. {platform}\n\nDetail: {e.Message}";
    }
}
