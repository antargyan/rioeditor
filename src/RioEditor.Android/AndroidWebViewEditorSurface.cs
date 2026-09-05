using System.Collections.Concurrent;
using Android.Print;
using Android.Webkit;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Java.Interop;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using RioEditor.Core.Export;
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
public sealed class AndroidWebViewEditorSurface : IEditorSurface, IWebViewTransport, IPdfExporter
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private WebViewHost? _host;
    private string? _failure;
    private string? _failureDetail;

    public bool IsAvailable => _failure is null;

    public string? UnavailableReason => _failure;

    public string? UnavailableDetail => _failureDetail;

    /// <summary>Never raised: the system WebView either constructs here or throws, synchronously.</summary>
    public event EventHandler? BecameUnavailable
    {
        add { }
        remove { }
    }

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
            _failure = "RioEditor shows your document using the Android System WebView, and it " +
                       "is missing or disabled on this device.\n\n" +
                       "Enable it under Settings → Apps → Android System WebView, or install it " +
                       "from the Play Store, then start RioEditor again.";
            _failureDetail = e.Message;
            // The shell checks IsAvailable as soon as this returns and shows the diagnostic panel.
            return new Panel();
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

    /// <summary>Activity context where one exists; the WebView prefers it too.</summary>
    private static global::Android.Content.Context? ActivityContext =>
        MainActivity.Current ?? (global::Android.Content.Context?)global::Android.App.Application.Context;

    // ------------------------------------------------------------------ IPdfExporter

    /// <summary>
    /// Android's WebView exposes no direct render-to-PDF API. It does expose a
    /// PrintDocumentAdapter, and Android's print framework always offers a "Save as PDF" target,
    /// so the print sheet is the honest native route rather than a workaround.
    /// </summary>
    public bool CanProducePdfBytes => false;

    public Task<byte[]?> ExportPdfBytesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<bool> TryShowPrintUiAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Must be the Activity, not Application.Context: the print framework shows UI and
                // silently refuses a non-Activity context.
                if (_host?.WebView is not { } webView ||
                    ActivityContext is not { } context ||
                    context.GetSystemService(global::Android.Content.Context.PrintService)
                        is not PrintManager printManager)
                {
                    completion.TrySetResult(false);
                    return;
                }

                const string jobName = "RioEditor document";
                var adapter = webView.CreatePrintDocumentAdapter(jobName);
                printManager.Print(jobName, adapter, new PrintAttributes.Builder().Build());
                completion.TrySetResult(true);
            }
            catch (Exception e)
            {
                // No print service on this device: let the caller fall back to window.print().
                global::Android.Util.Log.Warn("RioEditor", $"print UI unavailable: {e.Message}");
                completion.TrySetResult(false);
            }
        });

        return completion.Task;
    }

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
            // An Activity context where available: a WebView created with the Application context
            // cannot show dialogs, file pickers or the print UI.
            var context = ActivityContext ?? global::Android.App.Application.Context;
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
