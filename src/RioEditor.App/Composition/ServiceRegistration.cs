using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Services;
using RioEditor.App.ViewModels;
using RioEditor.Core.Editor;
using RioEditor.Core.Markdown;
using RioEditor.Core.Sanitization;
using RioEditor.Core.Settings;

namespace RioEditor.App.Composition;

/// <summary>
/// Everything that is identical across platforms. Each head then registers its own
/// <see cref="IEditorSurface"/> and <see cref="RioEditor.Core.Storage.IKeyValueStore"/>.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddRioEditor(this IServiceCollection services)
    {
        // --- Markdown pipeline ------------------------------------------------
        services.AddSingleton<IHtmlSanitizer, HtmlSanitizerService>();
        services.AddSingleton<IMarkdownService, MarkdownService>();
        services.AddSingleton<IHtmlToMarkdownService, HtmlToMarkdownService>();

        // --- Editor bridge ----------------------------------------------------
        services.AddSingleton<IWebViewBridge, WebViewBridge>();

        // --- Application services ---------------------------------------------
        services.AddSingleton<ITopLevelProvider, TopLevelProvider>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // --- View models -------------------------------------------------------
        services.AddSingleton<MainViewModel>();

        return services;
    }
}
