namespace RioEditor.App.Services;

/// <summary>
/// The document the app was launched to open, or null for a plain launch.
///
/// On Windows this is also how a file association arrives: a packaged full-trust app opened
/// through *Open with* is started with the file's path on its command line, exactly like a
/// double-click in a shell. So handling the command line handles both.
/// </summary>
public interface IStartupDocument
{
    string? Path { get; }
}

/// <summary>
/// Resolves the launch document from a command line. Heads that have no command line (WASM,
/// Android, iOS) get the <see cref="None"/> instance from the shared registration.
/// </summary>
public sealed class StartupDocument : IStartupDocument
{
    /// <summary>For platforms that are never launched with arguments.</summary>
    public static readonly IStartupDocument None = new StartupDocument(null);

    public StartupDocument(IReadOnlyList<string>? args) => Path = Resolve(args);

    public string? Path { get; }

    /// <summary>
    /// The first argument naming a file that actually exists. Everything else is skipped —
    /// switches, paths to files that have since been deleted, and the extra arguments a shell or
    /// debugger may append. A bad argument must never stop the editor from starting, so this
    /// never throws; the worst case is a normal empty session.
    /// </summary>
    private static string? Resolve(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var arg in args)
        {
            // Only '-' is treated as a switch marker: on Linux an absolute path starts with '/',
            // and a Windows '/switch' will simply fail the File.Exists test below anyway.
            if (string.IsNullOrWhiteSpace(arg) || arg[0] == '-')
            {
                continue;
            }

            try
            {
                // Resolve relative to the working directory, which is where a shell invocation
                // means them; the association hands over an absolute path already.
                var full = System.IO.Path.GetFullPath(arg);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException
                                          or UnauthorizedAccessException)
            {
                // Not a usable path — keep looking at the remaining arguments.
            }
        }

        return null;
    }
}
