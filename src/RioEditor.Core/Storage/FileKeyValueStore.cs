using System.Text.Json;
using RioEditor.Core.Editor;

namespace RioEditor.Core.Storage;

/// <summary>
/// Desktop/CLI implementation: one JSON document under
/// <c>%APPDATA%/RioEditor</c>, <c>~/Library/Application Support/RioEditor</c> or <c>~/.config/RioEditor</c>.
/// </summary>
public sealed class FileKeyValueStore : IKeyValueStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;

    public FileKeyValueStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "RioEditor");

        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    public async ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return map.GetValueOrDefault(key);
    }

    public async ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        map[key] = value;
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (map.Remove(key))
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is null)
            {
                if (File.Exists(_path))
                {
                    try
                    {
                        await using var stream = File.OpenRead(_path);
                        _cache = await JsonSerializer
                            .DeserializeAsync(stream, SettingsJsonContext.Default.DictionaryStringString,
                                cancellationToken)
                            .ConfigureAwait(false) ?? new Dictionary<string, string>();
                    }
                    catch (Exception e) when (e is JsonException or IOException)
                    {
                        // A corrupt settings file must never stop the editor from starting.
                        _cache = new Dictionary<string, string>();
                    }
                }
                else
                {
                    _cache = new Dictionary<string, string>();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return _cache;
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(_cache!, SettingsJsonContext.Default.DictionaryStringString);
            // Write-then-move so a crash mid-write cannot truncate the live file.
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Settings are best-effort; losing them is not worth crashing over.
        }
        finally
        {
            _gate.Release();
        }
    }
}
