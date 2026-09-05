using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;
using RioEditor.Platform.WebKitSurface;

namespace RioEditor.Desktop.MacOS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        App.App.Services = BuildServices();

        // No UseDesktopWebView() here: the surface is a plain NativeControlHost, so this head does
        // not depend on WebView.Avalonia at all.
        return AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, WkWebViewEditorSurface>()
            .BuildServiceProvider();
}
