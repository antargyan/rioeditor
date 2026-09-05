using UIKit;

namespace RioEditor.iOS;

public static class Application
{
    private static void Main(string[] args)
    {
        // UIApplicationMain hands control to AppDelegate, which boots Avalonia.
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
