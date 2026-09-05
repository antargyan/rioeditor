namespace RioEditor.App.Services;

public readonly record struct OpenedDocument(string? Path, string Text, string DisplayName);

public interface IFileService
{
    /// <summary>False on WebAssembly, where there is no writable file system.</summary>
    bool SupportsDirectFileAccess { get; }

    /// <summary>Shows a picker and reads the chosen Markdown file. Null when cancelled.</summary>
    Task<OpenedDocument?> OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes to <paramref name="path"/> when one is known and writable, otherwise falls back to
    /// a Save-As picker (which is a download on WASM). Returns the resulting path, if any.
    /// </summary>
    Task<string?> SaveAsync(string text, string? path, CancellationToken cancellationToken = default);

    Task<string?> SaveAsAsync(string text, string suggestedName, CancellationToken cancellationToken = default);

    /// <summary>Silent write used by autosave. Returns false when no in-place write was possible.</summary>
    Task<bool> WriteAsync(string path, string text, CancellationToken cancellationToken = default);

    Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a save picker for an exported artefact. <paramref name="extension"/> drives both the
    /// filter and the default suffix (e.g. "html", "pdf").
    /// </summary>
    Task<string?> SaveExportAsync(byte[] content, string suggestedName, string extension,
        string description, string mimeType, CancellationToken cancellationToken = default);

    bool Exists(string path);
}
