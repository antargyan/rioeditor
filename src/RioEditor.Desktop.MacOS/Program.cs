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
    public static void Main(string[] args) => BuildAvaloniaApp(args)
        .StartWithClassicDesktopLifetime(args);

    /// <summary>Avalonia's XAML previewer calls this overload by convention; it has no arguments.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        App.App.Services = BuildServices(args);

        // No UseDesktopWebView() here: the surface is a plain NativeControlHost, so this head does
        // not depend on WebView.Avalonia at all.
        return AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }

    private static IServiceProvider BuildServices(string[] args) =>
        new ServiceCollection()
            .AddRioEditor()
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, WkWebViewEditorSurface>()
            // Covers `open --args <file>` and a shell invocation. Documents opened by dropping
            // them on the app icon arrive through NSApplication instead, which this does not see.
            .AddSingleton<IStartupDocument>(new StartupDocument(args))
            .BuildServiceProvider();
}
