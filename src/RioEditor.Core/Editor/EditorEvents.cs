namespace RioEditor.Core.Editor;

/// <summary>Fired whenever the editing surface reports a settled change.</summary>
public sealed class DocumentChangedEventArgs : EventArgs
{
    public DocumentChangedEventArgs(string html, string markdown, int wordCount)
    {
        Html = html;
        Markdown = markdown;
        WordCount = wordCount;
    }

    public string Html { get; }

    public string Markdown { get; }

    public int WordCount { get; }
}

/// <summary>Document statistics that carry no dirty semantics (e.g. after opening a file).</summary>
public sealed class DocumentStatsEventArgs : EventArgs
{
    public DocumentStatsEventArgs(int wordCount) => WordCount = wordCount;

    public int WordCount { get; }
}

/// <summary>Caret/selection context, used to keep the toolbar's toggle states honest.</summary>
public sealed class SelectionChangedEventArgs : EventArgs
{
    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public bool InlineCode { get; init; }

    public int HeadingLevel { get; init; }

    public string BlockType { get; init; } = "p";
}
