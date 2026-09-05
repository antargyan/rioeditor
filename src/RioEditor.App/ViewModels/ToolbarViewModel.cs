using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using ReactiveUI;
using RioEditor.Core.Editor;

namespace RioEditor.App.ViewModels;

/// <summary>
/// Every toolbar button is a <see cref="ReactiveCommand"/> that forwards to the JS engine through
/// <see cref="IWebViewBridge"/>. Toggle states are pushed back by the engine on every selection
/// change, so the buttons reflect the caret rather than guessing.
/// </summary>
public sealed class ToolbarViewModel : ViewModelBase
{
    private readonly IWebViewBridge _bridge;

    private bool _isBold;
    private bool _isItalic;
    private bool _isInlineCode;
    private int _headingLevel;
    private string _linkUrl = string.Empty;
    private bool _isLinkEditorOpen;
    private bool _isDarkTheme;

    public ToolbarViewModel(IWebViewBridge bridge)
    {
        _bridge = bridge;

        Bold = ReactiveCommand.CreateFromTask(_bridge.ApplyBoldAsync);
        Italic = ReactiveCommand.CreateFromTask(_bridge.ApplyItalicAsync);
        Strikethrough = ReactiveCommand.CreateFromTask(_bridge.ApplyStrikethroughAsync);
        InlineCode = ReactiveCommand.CreateFromTask(_bridge.ApplyInlineCodeAsync);
        CodeBlock = ReactiveCommand.CreateFromTask(() => _bridge.ApplyCodeBlockAsync(string.Empty));
        Quote = ReactiveCommand.CreateFromTask(_bridge.ApplyQuoteAsync);
        BulletList = ReactiveCommand.CreateFromTask(_bridge.ApplyBulletListAsync);
        OrderedList = ReactiveCommand.CreateFromTask(_bridge.ApplyOrderedListAsync);
        TaskList = ReactiveCommand.CreateFromTask(_bridge.ApplyTaskListAsync);
        HorizontalRule = ReactiveCommand.CreateFromTask(_bridge.ApplyHorizontalRuleAsync);
        InsertTable = ReactiveCommand.CreateFromTask(() => _bridge.InsertTableAsync(3, 3));

        // Heading 1..6; 0 clears back to a paragraph.
        Heading = ReactiveCommand.CreateFromTask<int>(level => _bridge.ApplyHeadingAsync(level));

        OpenLinkEditor = ReactiveCommand.Create(() =>
        {
            LinkUrl = string.Empty;
            IsLinkEditorOpen = true;
        });

        var canApplyLink = this.WhenAnyValue(x => x.LinkUrl)
            .Select(url => !string.IsNullOrWhiteSpace(url));

        ApplyLink = ReactiveCommand.CreateFromTask(async () =>
        {
            await _bridge.ApplyLinkAsync(LinkUrl.Trim());
            IsLinkEditorOpen = false;
            LinkUrl = string.Empty;
        }, canApplyLink);

        CancelLink = ReactiveCommand.Create(() => { IsLinkEditorOpen = false; });

        _bridge.SelectionChanged += OnSelectionChanged;
    }

    public ReactiveCommand<Unit, Unit> Bold { get; }

    public ReactiveCommand<Unit, Unit> Italic { get; }

    public ReactiveCommand<Unit, Unit> Strikethrough { get; }

    public ReactiveCommand<Unit, Unit> InlineCode { get; }

    public ReactiveCommand<Unit, Unit> CodeBlock { get; }

    public ReactiveCommand<Unit, Unit> Quote { get; }

    public ReactiveCommand<Unit, Unit> BulletList { get; }

    public ReactiveCommand<Unit, Unit> OrderedList { get; }

    public ReactiveCommand<Unit, Unit> TaskList { get; }

    public ReactiveCommand<Unit, Unit> HorizontalRule { get; }

    public ReactiveCommand<Unit, Unit> InsertTable { get; }

    public ReactiveCommand<int, Unit> Heading { get; }

    public ReactiveCommand<Unit, Unit> OpenLinkEditor { get; }

    public ReactiveCommand<Unit, Unit> ApplyLink { get; }

    public ReactiveCommand<Unit, Unit> CancelLink { get; }

    public bool IsBold
    {
        get => _isBold;
        private set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    public bool IsItalic
    {
        get => _isItalic;
        private set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    public bool IsInlineCode
    {
        get => _isInlineCode;
        private set => this.RaiseAndSetIfChanged(ref _isInlineCode, value);
    }

    public int HeadingLevel
    {
        get => _headingLevel;
        private set => this.RaiseAndSetIfChanged(ref _headingLevel, value);
    }

    public string LinkUrl
    {
        get => _linkUrl;
        set => this.RaiseAndSetIfChanged(ref _linkUrl, value);
    }

    public bool IsLinkEditorOpen
    {
        get => _isLinkEditorOpen;
        set => this.RaiseAndSetIfChanged(ref _isLinkEditorOpen, value);
    }

    /// <summary>Bound to the theme switch; owned by <see cref="MainViewModel"/> which does the work.</summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => this.RaiseAndSetIfChanged(ref _isDarkTheme, value);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The engine posts from the WebView thread; ReactiveUI marshals to the UI scheduler.
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            IsBold = e.Bold;
            IsItalic = e.Italic;
            IsInlineCode = e.InlineCode;
            HeadingLevel = e.HeadingLevel;
        });
    }
}
