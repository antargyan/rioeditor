using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Microsoft.Extensions.DependencyInjection;
using RioEditor.App.Composition;
using RioEditor.App.Services;
using RioEditor.Core.Storage;

namespace RioEditor.Android;

/// <summary>
/// Android application object, and the thing that names the Avalonia app type.
///
/// This exists because Avalonia 12 changed the Android entry point: <c>AvaloniaMainActivity</c>
/// used to be generic (<c>AvaloniaMainActivity&lt;TApp&gt;</c>) and carried both the app type and
/// the <c>AppBuilder</c> customisation. It is now non-generic, and an
/// <c>AvaloniaAndroidApplication&lt;TApp&gt;</c> carries them instead — which is the more correct
/// shape anyway, since the Avalonia application outlives any single Activity.
/// </summary>
[Application]
public class RioApplication : AvaloniaAndroidApplication<App.App>
{
    protected RioApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // DI must be composed before Avalonia instantiates App.
        App.App.Services = BuildServices();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    private static IServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddRioEditor()
            // Android gives the app a private files directory, so the file-backed store works as-is.
            .AddSingleton<IKeyValueStore>(_ => new FileKeyValueStore())
            .AddSingleton<IEditorSurface, AndroidWebViewEditorSurface>()
            .BuildServiceProvider();
}
