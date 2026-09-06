using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;

namespace RioEditor.Desktop;

internal static class Program
{
    // Avalonia must be initialised before anything touches its APIs — hence no
    // SynchronizationContext work, no static state, nothing but the builder here.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp(args)
        .StartWithClassicDesktopLifetime(args);

    /// <summary>Avalonia's XAML previewer calls this overload by convention; it has no arguments.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp([]);

    public static AppBuilder BuildAvaloniaApp(string[] args)
    {
        App.App.Services = BuildServices(args);

        return AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static IServiceProvider BuildServices(string[] args) =>
        new ServiceCollection()
            .AddRioEditor()
            // Desktop-only registrations: a real file-backed settings store and the native WebView.
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, WebViewEditorSurface>()
            // Replaces the no-op default from AddRioEditor. This is also the .md file association:
            // Windows launches a packaged full-trust app with the opened file on its command line.
            .AddSingleton<IStartupDocument>(new StartupDocument(args))
            .BuildServiceProvider();
}
