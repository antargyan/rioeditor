using System.Runtime.Versioning;
using RioEditor.Core.Storage;

namespace RioEditor.Browser;

/// <summary>
/// localStorage-backed settings. Survives reloads and browser restarts; it does not survive the
/// user clearing site data, which is the closest thing the sandbox has to "deleting settings.json".
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserKeyValueStore : IKeyValueStore
{
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RioInterop.StorageGet(key));

    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        RioInterop.StorageSet(key, value);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        RioInterop.StorageRemove(key);
        return ValueTask.CompletedTask;
    }
}
