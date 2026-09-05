using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.ViewModels;
using RioEditor.Core.Settings;

namespace RioEditor.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Applies the persisted window size before the window is shown.</summary>
    private void RestoreGeometry()
    {
        var settings = App.Services.GetService<ISettingsService>();
        if (settings is null)
        {
            return;
        }

        // Settings are loaded asynchronously by the view model; the values already on the
        // instance are the defaults until then, which is exactly what we want for first run.
        var current = settings.Current;
        if (current.WindowWidth > 200 && current.WindowHeight > 200)
        {
            Width = current.WindowWidth;
            Height = current.WindowHeight;
        }

        if (current.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            // Fire-and-forget: settings persistence must not block the close.
            _ = viewModel.PersistWindowStateAsync(Width, Height, WindowState == WindowState.Maximized);
        }

        base.OnClosing(e);
    }
}
