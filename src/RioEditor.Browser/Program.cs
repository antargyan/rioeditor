using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;

namespace RioEditor.Browser;

[SupportedOSPlatform("browser")]
internal static class Program
{
    private static async Task Main(string[] args)
    {
        // The interop module must be importable before any surface or store is resolved.
        await RioInterop.InitializeAsync();

        App.App.Services = BuildServices();

        await BuildAvaloniaApp().StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>()
            .WithInterFont();

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            // WASM-only registrations: localStorage settings and the iframe-backed surface.
            .AddSingleton<IKeyValueStore, BrowserKeyValueStore>()
            .AddSingleton<IEditorSurface, BrowserEditorSurface>()
            .BuildServiceProvider();
}
