using System.Text.Json;
using RioEditor.Core.Editor;
using RioEditor.Core.Models;
using RioEditor.Core.Storage;

namespace RioEditor.Core.Settings;

/// <summary>
/// Serializes <see cref="AppSettings"/> through whichever <see cref="IKeyValueStore"/> the host
/// registered, so the same code path persists to disk on desktop and to localStorage on WASM.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string StorageKey = "rio.settings";

    private readonly IKeyValueStore _store;

    public SettingsService(IKeyValueStore store) => _store = store;

    public AppSettings Current { get; private set; } = new();

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await _store.GetAsync(StorageKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                Current = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
            catch (JsonException)
            {
                Current = new AppSettings();
            }
        }

        return Current;
    }

    public ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
        return _store.SetAsync(StorageKey, json, cancellationToken);
    }
}
