using AvaloniaWebView;

namespace RioEditor.Desktop;

/// <summary>
/// Desktop specialisation of the shared <see cref="RioEditor.App.App"/>. The only difference is
/// that the WebView backend has to be registered during Avalonia's service registration phase.
/// </summary>
public sealed class DesktopApp : App.App
{
    public override void RegisterServices()
    {
        base.RegisterServices();

        // Selects WebView2 / WebKitGTK / WKWebView according to the running OS.
        AvaloniaWebViewBuilder.Initialize(default);
    }
}
