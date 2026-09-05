namespace RioEditor.Core.Storage;

/// <summary>
/// Tiny persistence primitive. Desktop backs it with a JSON file under the user's app-data folder;
/// the WASM head backs it with <c>window.localStorage</c>. Everything above this interface is shared.
/// </summary>
public interface IKeyValueStore
{
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}
