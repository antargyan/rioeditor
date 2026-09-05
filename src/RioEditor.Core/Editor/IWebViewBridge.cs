using RioEditor.Core.Models;

namespace RioEditor.Core.Editor;

/// <summary>
/// Typed façade over the JavaScript editor engine. Every method here maps 1:1 to a function in
/// <c>editor.js</c>; the transport carries the messages.
/// </summary>
public interface IWebViewBridge
{
    bool IsReady { get; }

    event EventHandler? Ready;

    event EventHandler<DocumentChangedEventArgs>? DocumentChanged;

    event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

    /// <summary>Raised when the document's size changes without the document becoming dirty.</summary>
    event EventHandler<DocumentStatsEventArgs>? StatsChanged;

    /// <summary>Binds the bridge to a platform surface and loads the editor document.</summary>
    Task AttachAsync(IWebViewTransport transport, AppTheme theme, bool allowRemoteScripts);

    /// <summary>Replaces the whole document (open file, new file, revert).</summary>
    Task SetMarkdownAsync(string markdown, CancellationToken cancellationToken = default);

    /// <summary>Pulls the current buffer back as Markdown (save, autosave, copy-as-markdown).</summary>
    Task<string> GetMarkdownAsync(CancellationToken cancellationToken = default);

    Task ApplyBoldAsync();

    Task ApplyItalicAsync();

    Task ApplyStrikethroughAsync();

    Task ApplyInlineCodeAsync();

    Task ApplyHeadingAsync(int level);

    Task ApplyLinkAsync(string url, string? text = null);

    Task ApplyCodeBlockAsync(string language = "");

    Task ApplyQuoteAsync();

    Task ApplyBulletListAsync();

    Task ApplyOrderedListAsync();

    Task ApplyTaskListAsync();

    Task InsertTableAsync(int rows = 3, int columns = 3);

    Task ApplyHorizontalRuleAsync();

    Task ToggleThemeAsync();

    Task SetThemeAsync(AppTheme theme);

    Task FocusAsync();

    /// <summary>
    /// Asks the document to print itself (<c>window.print()</c>). The last-resort PDF route on
    /// platforms whose WebView exposes neither PDF bytes nor a native print UI.
    /// </summary>
    Task PrintAsync();
}
