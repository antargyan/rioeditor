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

        var host = this.FindControl<ContentControl>("EditorHost")!;
        var fallback = this.FindControl<Border>("SurfaceFallback")!;

        if (viewModel.Surface.IsAvailable)
        {
            host.Content = viewModel.Surface.CreateView();
        }
        else
        {
            fallback.IsVisible = true;
            this.FindControl<TextBlock>("SurfaceFallbackReason")!.Text =
                viewModel.Surface.UnavailableReason ?? "Unknown reason.";
        }

        await viewModel.InitializeAsync();
    }
}
