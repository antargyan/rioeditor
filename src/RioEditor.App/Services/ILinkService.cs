using Avalonia.Platform.Storage;

namespace RioEditor.App.Services;

/// <summary>Opens a URL outside the app, using Avalonia's launcher so every head behaves the same.</summary>
public interface ILinkService
{
    Task<bool> OpenAsync(Uri uri);
}

public sealed class LinkService : ILinkService
{
    private readonly ITopLevelProvider _topLevel;

    public LinkService(ITopLevelProvider topLevel) => _topLevel = topLevel;

    public async Task<bool> OpenAsync(Uri uri)
    {
        if (_topLevel.TopLevel?.Launcher is not { } launcher)
        {
            return false;
        }

        try
        {
            return await launcher.LaunchUriAsync(uri).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // No handler for the scheme, or the platform refused: the caller reports it.
            return false;
        }
    }
}
