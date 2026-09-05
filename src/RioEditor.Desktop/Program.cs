using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.WebView.Desktop;
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
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        App.App.Services = BuildServices();

        return AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI()
            .UseDesktopWebView();
    }

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            // Desktop-only registrations: a real file-backed settings store and the native WebView.
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, WebViewEditorSurface>()
            .BuildServiceProvider();
}
