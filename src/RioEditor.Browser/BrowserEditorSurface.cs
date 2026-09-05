using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RioEditor.App.Services;
using RioEditor.Core.Editor;

namespace RioEditor.Browser;

/// <summary>
/// WebAssembly editing surface.
///
/// The browser sandbox has no WebView control, so the editor document lives in a same-origin
/// <c>&lt;iframe srcdoc&gt;</c> layered over the Avalonia canvas. A transparent placeholder control
/// occupies the corresponding slot in the Avalonia layout and reports its bounds to JavaScript, so
/// the frame tracks window resizes, toolbar height changes and the link editor opening.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserEditorSurface : IEditorSurface, IWebViewTransport
{
    private readonly ConcurrentQueue<string> _pendingScripts = new();

    private Border? _placeholder;
    private DispatcherTimer? _flushTimer;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string? UnavailableDetail => null;

    /// <summary>Never raised: in WASM the browser itself is the WebView, so there is nothing to miss.</summary>
    public event EventHandler? BecameUnavailable
    {
        add { }
        remove { }
    }

    public IWebViewTransport Transport => this;

    public Control CreateView()
    {
        if (_placeholder is not null)
        {
            return _placeholder;
        }

        // Transparent: the iframe on top is what the user actually sees.
        _placeholder = new Border { Background = Avalonia.Media.Brushes.Transparent };

        // Any layout change (resize, toolbar growing, link editor opening) repositions the frame.
        _placeholder.LayoutUpdated += (_, _) => UpdateBounds();
        _placeholder.AttachedToVisualTree += (_, _) => UpdateBounds();
        _placeholder.DetachedFromVisualTree += (_, _) => RioInterop.SetVisible(false);

        return _placeholder;
    }

    private void UpdateBounds()
    {
        if (_placeholder is null || TopLevel.GetTopLevel(_placeholder) is not { } topLevel)
        {
            return;
        }

        var origin = _placeholder.TranslatePoint(new Point(0, 0), topLevel);
        if (origin is not { } point)
        {
            return;
        }

        RioInterop.SetBounds(point.X, point.Y, _placeholder.Bounds.Width, _placeholder.Bounds.Height);
    }

    // ------------------------------------------------------------------ IWebViewTransport

    public async Task LoadHtmlAsync(string html, CancellationToken cancellationToken = default)
    {
        await RioInterop.InitializeAsync().ConfigureAwait(true);

        RioInterop.RegisterMessageHandler(OnMessage);
        RioInterop.LoadHtml(html);
        UpdateBounds();

        StartScriptFlushLoop();
    }

    public Task ExecuteScriptAsync(string script, CancellationToken cancellationToken = default)
    {
        // srcdoc parsing is asynchronous; queue until window.rio exists inside the frame.
        if (!RioInterop.IsLoaded())
        {
            _pendingScripts.Enqueue(script);
            return Task.CompletedTask;
        }

        RioInterop.ExecuteScript(script);
        return Task.CompletedTask;
    }

    public event EventHandler<string>? MessageReceived;

    private void OnMessage(string payload) => MessageReceived?.Invoke(this, payload);

    /// <summary>
    /// Drains queued scripts once the frame document is live. A short polling timer is simpler and
    /// more robust here than an onload handler, which srcdoc frames fire inconsistently.
    /// </summary>
    private void StartScriptFlushLoop()
    {
        _flushTimer?.Stop();
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            if (!RioInterop.IsLoaded())
            {
                return;
            }

            while (_pendingScripts.TryDequeue(out var script))
            {
                RioInterop.ExecuteScript(script);
            }
        });

        _flushTimer.Start();
    }
}
