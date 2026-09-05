using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RioEditor.Core.Models;

/// <summary>
/// The single in-memory representation of the document being edited.
/// Deliberately free of ReactiveUI so that <c>RioEditor.Core</c> stays UI-framework agnostic
/// (it is referenced by the WASM head where the reactive stack is heavier than we need).
/// </summary>
public sealed class DocumentModel : INotifyPropertyChanged
{
    private string _markdown = string.Empty;
    private string _html = string.Empty;
    private string? _filePath;
    private bool _isDirty;

    /// <summary>Canonical Markdown source. Produced by the HTML -> Markdown reverse pipeline.</summary>
    public string Markdown
    {
        get => _markdown;
        set => Set(ref _markdown, value);
    }

    /// <summary>Sanitized HTML currently mounted in the editing surface.</summary>
    public string Html
    {
        get => _html;
        set => Set(ref _html, value);
    }

    /// <summary>Absolute path on disk, or <c>null</c> for an unsaved buffer (always null on WASM).</summary>
    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (Set(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileName));
            }
        }
    }

    /// <summary>True when the buffer has edits that are not yet persisted.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        set => Set(ref _isDirty, value);
    }

    public string FileName =>
        string.IsNullOrEmpty(_filePath) ? "Untitled.md" : Path.GetFileName(_filePath);

    /// <summary>Resets the buffer to an empty, clean, path-less state.</summary>
    public void Reset()
    {
        Markdown = string.Empty;
        Html = string.Empty;
        FilePath = null;
        IsDirty = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
