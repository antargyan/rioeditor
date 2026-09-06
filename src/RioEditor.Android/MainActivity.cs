using Android.App;
using Android.Content.PM;
using Avalonia.Android;
using Avalonia.Media;

namespace RioEditor.Android;

/// <summary>
/// Android entry point. Avalonia runs under the single-view lifetime, so the shared
/// <c>App</c> mounts <c>MainView</c> rather than creating a window.
///
/// The app type and the <c>AppBuilder</c> customisation live on <see cref="RioApplication"/>:
/// Avalonia 12 moved them off the Activity.
/// </summary>
[Activity(
    Label = "RioEditor",
    Theme = "@style/RioTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    // Handle rotation ourselves; recreating the Activity would tear down the WebView and the
    // document along with it.
    ConfigurationChanges = ConfigChanges.Orientation
                           | ConfigChanges.ScreenSize
                           | ConfigChanges.UiMode
                           | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// The running Activity. Some Android APIs — the print framework in particular — need an
    /// Activity context and fail with the Application one, because they have UI to show.
    /// </summary>
    internal static MainActivity? Current { get; private set; }

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
    }
}
