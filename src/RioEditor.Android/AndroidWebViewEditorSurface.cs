using System.Collections.Concurrent;
using Android.Webkit;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Java.Interop;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using AndroidWebView = Android.Webkit.WebView;

namespace RioEditor.Android;

/// <summary>
/// Android editing surface: the system <see cref="AndroidWebView"/> (Chrome-backed) hosted in an
/// Avalonia <see cref="NativeControlHost"/>.
///
/// The transport differs from Apple's only in the inbound channel: Android has no
/// <c>webkit.messageHandlers</c>, so a <c>@JavascriptInterface</c> object is injected as
/// <c>window.rioAndroid</c>, which <c>postToHost</c> in editor.js knows how to use.
/// </summary>
public sealed class AndroidWebViewEditorSurface : IEditorSurface, IWebViewTransport
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private WebViewHost? _host;
    private string? _failure;

    public bool IsAvailable => _failure is null;

    public string? UnavailableReason => _failure;

    public IWebViewTransport Transport => this;

    public Control CreateView()
    {
        if (_host is not null)
        {
            return _host;
        }

        try
        {
            _host = new WebViewHost(this);
            return _host;
        }
        catch (Exception e)
        {
            // Android Go devices and some custom ROMs ship without a system WebView package.
            _failure = "The Android System WebView is missing or disabled. Enable it in " +
                       $"Settings → Apps → Android System WebView.\n\nDetail: {e.Message}";
            return new TextBlock { Text = _failure, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
    }

    // ------------------------------------------------------------------ IWebViewTransport

    public Task LoadHtmlAsync(string html, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_host?.WebView is not { } webView)
            {
                _host?.QueueInitialHtml(html);
                return;
            }

            _host.DocumentLoaded = false;
            // A null base URL keeps the document origin-less, matching the other platforms.
            webView.LoadDataWithBaseURL(null, html, "text/html", "utf-8", null);
        }).GetTask();

    public Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        if (_host?.WebView is null || !_host.DocumentLoaded)
        {
            _pendingScripts.Enqueue(script);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Evaluate(script)).GetTask();
    }

    public event EventHandler<string>? MessageReceived;

    // ------------------------------------------------------------------ internals

    private void Evaluate(string script) => _host?.WebView?.EvaluateJavascript(script, null);

    internal void RaiseMessage(string payload) =>
        // The JavascriptInterface callback arrives on a WebView worker thread.
        Dispatcher.UIThread.Post(() => MessageReceived?.Invoke(this, payload));

    internal void FlushPendingScripts()
    {
        while (_pendingScripts.TryDequeue(out var script))
        {
            Evaluate(script);
        }
    }

    // ------------------------------------------------------------------ native host

    private sealed class WebViewHost : NativeControlHost
    {
        private readonly AndroidWebViewEditorSurface _owner;
        private string? _initialHtml;

        public WebViewHost(AndroidWebViewEditorSurface owner) => _owner = owner;

        public AndroidWebView? WebView { get; private set; }

        public bool DocumentLoaded { get; set; }

        public void QueueInitialHtml(string html) => _initialHtml = html;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var context = global::Android.App.Application.Context;
            var webView = new AndroidWebView(context);

            webView.Settings.JavaScriptEnabled = true;
            webView.Settings.DomStorageEnabled = true;
            // The document is inlined, so no file or content access is ever needed.
            webView.Settings.AllowFileAccess = false;
            webView.Settings.AllowContentAccess = false;

            webView.AddJavascriptInterface(new JavascriptBridge(_owner), "rioAndroid");
            webView.SetWebViewClient(new EditorWebViewClient(_owner, this));

            WebView = webView;

            if (_initialHtml is not null)
            {
                webView.LoadDataWithBaseURL(null, _initialHtml, "text/html", "utf-8", null);
                _initialHtml = null;
            }

            return new AndroidViewControlHandle(webView);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            WebView?.Destroy();
            WebView = null;
        }
    }

    /// <summary>Injected as <c>window.rioAndroid</c>; the engine calls <c>postMessage</c> on it.</summary>
    private sealed class JavascriptBridge : Java.Lang.Object
    {
        private readonly AndroidWebViewEditorSurface _owner;

        public JavascriptBridge(AndroidWebViewEditorSurface owner) => _owner = owner;

        [JavascriptInterface]
        [Export("postMessage")]
        public void PostMessage(string json) => _owner.RaiseMessage(json);
    }

    private sealed class EditorWebViewClient : WebViewClient
    {
        private readonly AndroidWebViewEditorSurface _owner;
        private readonly WebViewHost _host;

        public EditorWebViewClient(AndroidWebViewEditorSurface owner, WebViewHost host)
        {
            _owner = owner;
            _host = host;
        }

        public override void OnPageFinished(AndroidWebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            _host.DocumentLoaded = true;
            _owner.FlushPendingScripts();
        }

        public override bool ShouldOverrideUrlLoading(AndroidWebView? view,
            IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            // Links open in the user's browser; the editing surface never navigates away.
            try
            {
                var intent = new global::Android.Content.Intent(
                    global::Android.Content.Intent.ActionView,
                    global::Android.Net.Uri.Parse(url));
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch (Exception)
            {
                // No browser installed, or the scheme is unhandled: swallow it.
            }

            return true;
        }
    }
}
