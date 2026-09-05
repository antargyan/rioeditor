using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Services;
using RioEditor.App.ViewModels;

namespace RioEditor.App.Views;

public partial class MainView : UserControl
{
    /// <summary>
    /// Below this width the chrome switches to the compact layout. Driven by the control's own
    /// size rather than a device check, so a narrow desktop window compacts too — and a tablet or
    /// a landscape phone keeps the full toolbar.
    /// </summary>
    private const double CompactBreakpoint = 700;

    private bool _initialized;

    public MainView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsCompact = e.NewSize.Width < CompactBreakpoint;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The WebView can only be created once there is a real visual tree (and, on desktop, a native
    /// window handle), so surface creation and bridge start-up happen here rather than in the ctor.
    /// </summary>
    protected override async void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_initialized || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _initialized = true;

        // The first size change can arrive before DataContext is set, so seed it here too.
        viewModel.IsCompact = Bounds.Width > 0 && Bounds.Width < CompactBreakpoint;

        // Publish the TopLevel so the file service can reach the storage provider.
        App.Services.GetRequiredService<ITopLevelProvider>().TopLevel = TopLevel.GetTopLevel(this);

        // A surface cannot report whether its backend exists until it has tried to build one, so
        // create first and ask afterwards. Checking IsAvailable before this call always saw
        // "available" and left the diagnostic panel unreachable.
        this.FindControl<ContentControl>("EditorHost")!.Content = viewModel.Surface.CreateView();
        ShowFallbackIfUnavailable();

        // On Windows and Android the native backend is built asynchronously, so a missing runtime
        // is reported after CreateView has already returned a control.
        viewModel.Surface.BecameUnavailable += OnSurfaceBecameUnavailable;

        await viewModel.InitializeAsync();
    }

    private void OnSurfaceBecameUnavailable(object? sender, EventArgs e) => ShowFallbackIfUnavailable();

    private void ShowFallbackIfUnavailable()
    {
        if (DataContext is not MainViewModel viewModel || viewModel.Surface.IsAvailable)
        {
            return;
        }

        // Hide the host as well: a half-built native WebView can otherwise keep painting over the
        // panel, which is exactly the blank-window symptom this is here to replace.
        this.FindControl<ContentControl>("EditorHost")!.IsVisible = false;
        this.FindControl<Border>("SurfaceFallback")!.IsVisible = true;

        this.FindControl<SelectableTextBlock>("SurfaceFallbackReason")!.Text =
            viewModel.Surface.UnavailableReason ?? "No WebView is available on this platform.";

        var detail = this.FindControl<SelectableTextBlock>("SurfaceFallbackDetail")!;
        detail.Text = Condense(viewModel.Surface.UnavailableDetail);
        detail.IsVisible = detail.Text is not null;
    }

    /// <summary>
    /// Backend failures arrive with a full stack trace attached. Enough of it to identify the
    /// error in a bug report is useful; all of it buries the instructions above it.
    /// </summary>
    private static string? Condense(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        const int limit = 220;
        var firstLine = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return firstLine.Length <= limit ? firstLine : firstLine[..limit].TrimEnd() + "…";
    }
}
