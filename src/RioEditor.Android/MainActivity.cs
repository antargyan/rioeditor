using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;

namespace RioEditor.Android;

/// <summary>
/// Android entry point. Avalonia runs under the single-view lifetime, so the shared
/// <c>App</c> mounts <c>MainView</c> rather than creating a window.
/// </summary>
[Activity(
    Label = "RioEditor",
    Theme = "@style/RioTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // Handle rotation ourselves; recreating the Activity would tear down the WebView and the
    // document along with it.
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode
                           | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity<App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // DI must be composed before Avalonia instantiates App.
        App.App.Services = BuildServices();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            // Android gives the app a private files directory, so the file-backed store works as-is.
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, AndroidWebViewEditorSurface>()
            .BuildServiceProvider();
}
