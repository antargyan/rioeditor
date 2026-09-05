using Avalonia;
using Avalonia.iOS;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;
using RioEditor.Platform.WebKitSurface;

namespace RioEditor.iOS;

/// <summary>
/// iOS entry point. Avalonia runs under the single-view lifetime here, so the shared
/// <c>App</c> mounts <c>MainView</c> directly instead of creating a window.
/// </summary>
[Register(nameof(AppDelegate))]
public partial class AppDelegate : AvaloniaAppDelegate<App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // DI has to be composed before Avalonia instantiates App.
        App.App.Services = BuildServices();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            // The iOS sandbox gives every app its own writable Documents/Library directory, so the
            // ordinary file-backed settings store works unchanged.
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, WkWebViewEditorSurface>()
            .BuildServiceProvider();
}
