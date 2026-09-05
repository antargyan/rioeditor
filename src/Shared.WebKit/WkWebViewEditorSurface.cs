using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using CoreGraphics;
using Foundation;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using RioEditor.Core.Export;
using WebKit;

#if IOS
using UIKit;
using NativeView = UIKit.UIView;
#else
using AppKit;
using NativeView = AppKit.NSView;
#endif

namespace RioEditor.Platform.WebKitSurface;

/// <summary>
/// Editing surface backed by a real <see cref="WKWebView"/>, shared by the macOS and iOS heads.
///
/// The two platforms differ only in trivia — WKWebView is an <c>NSView</c> on macOS and a
/// <c>UIView</c> on iOS, and opening an external link goes through NSWorkspace or UIApplication —
/// so one file with a pair of <c>#if IOS</c> switches beats two copies that drift apart.
///
/// Why this exists at all: the cross-platform WebView.Avalonia package resolves its Apple backends
/// through legacy Xamarin.Mac bindings, whose type initializer throws on the .NET 10 runtime.
/// Targeting <c>net10.0-macos</c> / <c>net10.0-ios</c> gives us the modern WebKit bindings instead,
/// and Avalonia's <see cref="NativeControlHost"/> drops the resulting native view into the layout.
///
/// The engine already speaks this transport: <c>postToHost</c> in editor.js posts to
/// <c>window.webkit.messageHandlers.rio</c>, which is the handler registered below.
/// </summary>
public sealed class WkWebViewEditorSurface : IEditorSurface, IWebViewTransport, IPdfExporter
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private WebViewHost? _host;
    private string? _failure;
    private string? _failureDetail;

    public bool IsAvailable => _failure is null;

    public string? UnavailableReason => _failure;

    public string? UnavailableDetail => _failureDetail;

    /// <summary>Never raised: WKWebView is part of the OS and constructs synchronously or throws.</summary>
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
            _failure = "RioEditor shows your document using WKWebView, which is part of the " +
                       "system, so this failure is unexpected.\n\n" +
                       "Restarting RioEditor may clear it. If it keeps happening, please report " +
                       "the details below.";
            _failureDetail = e.Message;
            // The shell checks IsAvailable as soon as this returns and shows the diagnostic panel.
            return new Panel();
        }
    }

    // ------------------------------------------------------------------ IWebViewTransport

    public Task LoadHtmlAsync(string html, CancellationToken cancellationToken = default) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var webView = _host?.WebView;
            if (webView is null)
            {
                // The native control is created lazily on attach; replay once it exists.
                _host?.QueueInitialHtml(html);
                return;
            }

            _host!.DocumentLoaded = false;
            webView.LoadHtmlString(new NSString(html), null);
        }).GetTask();

    public Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        if (_host?.WebView is null || !_host.DocumentLoaded)
        {
            // Ordered replay once the document reports didFinishNavigation.
            _pendingScripts.Enqueue(script);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Evaluate(script)).GetTask();
    }

    public event EventHandler<string>? MessageReceived;

    // ------------------------------------------------------------------ IPdfExporter

    /// <summary>WKWebView renders the PDF with the very engine that drew the document on screen.</summary>
    public bool CanProducePdfBytes => _host?.WebView is not null;

    public Task<byte[]?> ExportPdfBytesAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            if (_host?.WebView is not { } webView)
            {
                completion.TrySetResult(null);
                return;
            }

            // A default configuration means "the whole document", not just the visible viewport.
            webView.CreatePdf(new WKPdfConfiguration(), (data, error) =>
            {
                if (error is not null || data is null)
                {
                    completion.TrySetResult(null);
                    return;
                }

                var bytes = new byte[(int)data.Length];
                System.Runtime.InteropServices.Marshal.Copy(data.Bytes, bytes, 0, bytes.Length);
                completion.TrySetResult(bytes);
            });
        });

        return completion.Task;
    }

    /// <summary>Not needed: PDF bytes are always available, so the print UI is never the best route.</summary>
    public Task<bool> TryShowPrintUiAsync() => Task.FromResult(false);

    // ------------------------------------------------------------------ internals

    private void Evaluate(string script)
    {
        var webView = _host?.WebView;
        if (webView is null)
        {
            return;
        }

        webView.EvaluateJavaScript(new NSString(script), (_, error) =>
        {
            if (error is not null)
            {
                // A script error must not escalate into a managed exception on the UI thread.
                Console.Error.WriteLine($"[RioEditor] script error: {error.LocalizedDescription}");
            }
        });
    }

    internal void RaiseMessage(string payload) => MessageReceived?.Invoke(this, payload);

    internal void FlushPendingScripts()
    {
        while (_pendingScripts.TryDequeue(out var script))
        {
            Evaluate(script);
        }
    }

    // ------------------------------------------------------------------ native host

    /// <summary>
    /// Bridges Avalonia's visual tree to an NSView. Avalonia creates the native control when the
    /// host is attached, which is why the first LoadHtml may arrive before the WKWebView exists.
    /// </summary>
    private sealed class WebViewHost : NativeControlHost
    {
        private readonly WkWebViewEditorSurface _owner;
        private string? _initialHtml;

        public WebViewHost(WkWebViewEditorSurface owner) => _owner = owner;

        public WKWebView? WebView { get; private set; }

        public bool DocumentLoaded { get; set; }

        public void QueueInitialHtml(string html) => _initialHtml = html;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var configuration = new WKWebViewConfiguration();

            // window.webkit.messageHandlers.rio.postMessage(json) -> DidReceiveScriptMessage
            configuration.UserContentController.AddScriptMessageHandler(
                new ScriptMessageHandler(_owner), "rio");

            // Let the page keep its own transparent background so the Avalonia theme shows through
            // during load instead of a white flash.
            configuration.Preferences.JavaScriptCanOpenWindowsAutomatically = false;

            var webView = new WKWebView(new CGRect(0, 0, 800, 600), configuration)
            {
                NavigationDelegate = new NavigationDelegate(_owner, this)
            };

            WebView = webView;

            if (_initialHtml is not null)
            {
                webView.LoadHtmlString(new NSString(_initialHtml), null);
                _initialHtml = null;
            }

            return new NativeViewHandle(webView);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            WebView?.Dispose();
            WebView = null;
        }
    }

    /// <summary>Wraps an NSView pointer in the shape Avalonia's macOS backend expects.</summary>
    private sealed class NativeViewHandle : IPlatformHandle
    {
        public NativeViewHandle(NativeView view) => Handle = view.Handle.Handle;

        public IntPtr Handle { get; }

        /// <summary>Avalonia's Apple backends key off these exact descriptor strings.</summary>
#if IOS
        public string HandleDescriptor => "UIView";
#else
        public string HandleDescriptor => "NSView";
#endif
    }

    private sealed class ScriptMessageHandler : NSObject, IWKScriptMessageHandler
    {
        private readonly WkWebViewEditorSurface _owner;

        public ScriptMessageHandler(WkWebViewEditorSurface owner) => _owner = owner;

        public void DidReceiveScriptMessage(WKUserContentController userContentController,
            WKScriptMessage message)
        {
            var payload = message.Body?.ToString();
            if (!string.IsNullOrEmpty(payload))
            {
                _owner.RaiseMessage(payload);
            }
        }
    }

    private sealed class NavigationDelegate : WKNavigationDelegate
    {
        private readonly WkWebViewEditorSurface _owner;
        private readonly WebViewHost _host;

        public NavigationDelegate(WkWebViewEditorSurface owner, WebViewHost host)
        {
            _owner = owner;
            _host = host;
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            _host.DocumentLoaded = true;
            _owner.FlushPendingScripts();
        }

        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction,
            Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request?.Url;
            var scheme = url?.Scheme?.ToLowerInvariant();

            // The editor document itself loads with an about: URL; everything else is a link the
            // user clicked, which belongs in their browser rather than in the editing surface.
            if (url is null || scheme is null or "about" or "file")
            {
                decisionHandler(WKNavigationActionPolicy.Allow);
                return;
            }

            decisionHandler(WKNavigationActionPolicy.Cancel);

            if (scheme is "http" or "https" or "mailto")
            {
#if IOS
                UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
#else
                NSWorkspace.SharedWorkspace.OpenUrl(url);
#endif
            }
        }
    }
}
