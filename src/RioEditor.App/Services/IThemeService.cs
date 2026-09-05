using Avalonia;
using Avalonia.Styling;
using RioEditor.Core.Models;

namespace RioEditor.App.Services;

/// <summary>Keeps the Avalonia chrome (toolbar, status bar) in step with the WebView theme.</summary>
public interface IThemeService
{
    AppTheme Current { get; }

    void Apply(AppTheme theme);
}

public sealed class ThemeService : IThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Light;

    public void Apply(AppTheme theme)
    {
        Current = theme;
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = theme == AppTheme.Dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }
}
