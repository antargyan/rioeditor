using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.ViewModels;
using RioEditor.App.Views;

namespace RioEditor.App;

public partial class App : Application
{
    /// <summary>
    /// Set by the platform head before <c>Start</c>. Heads differ only in which
    /// <c>IEditorSurface</c> / <c>IKeyValueStore</c> they register.
    /// </summary>
    public static IServiceProvider Services { get; set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var mainViewModel = Services.GetRequiredService<MainViewModel>();

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                break;

            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView { DataContext = mainViewModel };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
