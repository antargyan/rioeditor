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
    /// <summary>
    /// The running Activity. Some Android APIs — the print framework in particular — need an
    /// Activity context and fail with the Application one, because they have UI to show.
    /// </summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
    }

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
