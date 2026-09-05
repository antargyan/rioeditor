using System.Text;
using Avalonia.Platform.Storage;

namespace RioEditor.App.Services;

/// <summary>
/// File I/O through Avalonia's <see cref="IStorageProvider"/>, so the same code path works on
/// desktop (real dialogs) and in the browser (File System Access API, with a download fallback).
/// </summary>
public sealed class FileService : IFileService
{
    private static readonly FilePickerFileType MarkdownFileType = new("Markdown")
    {
        Patterns = ["*.md", "*.markdown", "*.mdown", "*.mkd", "*.txt"],
        AppleUniformTypeIdentifiers = ["net.daringfireball.markdown", "public.plain-text"],
        MimeTypes = ["text/markdown", "text/plain"]
    };

    private readonly ITopLevelProvider _topLevel;

    public FileService(ITopLevelProvider topLevel) => _topLevel = topLevel;

    public bool SupportsDirectFileAccess => !OperatingSystem.IsBrowser();

    private IStorageProvider? Storage => _topLevel.TopLevel?.StorageProvider;

    public async Task<OpenedDocument?> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown document",
            AllowMultiple = false,
            FileTypeFilter = [MarkdownFileType, FilePickerFileTypes.All]
        }).ConfigureAwait(true);

        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true);

        return new OpenedDocument(file.TryGetLocalPath(), text, file.Name);
    }

    public async Task<string?> SaveAsync(string text, string? path, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(path) && SupportsDirectFileAccess)
        {
            return await WriteAsync(path, text, cancellationToken).ConfigureAwait(true) ? path : null;
        }

        var suggested = string.IsNullOrEmpty(path) ? "Untitled.md" : Path.GetFileName(path);
        return await SaveAsAsync(text, suggested, cancellationToken).ConfigureAwait(true);
    }

    public async Task<string?> SaveAsAsync(string text, string suggestedName,
        CancellationToken cancellationToken = default)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Markdown document",
            SuggestedFileName = suggestedName,
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices = [MarkdownFileType]
        }).ConfigureAwait(true);

        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(true);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(true);

        return file.TryGetLocalPath();
    }

    public async Task<string?> SaveExportAsync(byte[] content, string suggestedName, string extension,
        string description, string mimeType, CancellationToken cancellationToken = default)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var fileType = new FilePickerFileType(description)
        {
            Patterns = [$"*.{extension}"],
            MimeTypes = [mimeType]
        };

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export as {description}",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [fileType]
        }).ConfigureAwait(true);

        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(true);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(true);

        // On WASM this is a download, so there is no path to report back.
        return file.TryGetLocalPath();
    }

    public async Task<bool> WriteAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (!SupportsDirectFileAccess)
        {
            return false;
        }

        try
        {
            // Atomic-ish write: a crash during autosave must not truncate the user's document.
            var temp = path + ".rio-tmp";
            await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!SupportsDirectFileAccess || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Exists(string path) => SupportsDirectFileAccess && File.Exists(path);
}
