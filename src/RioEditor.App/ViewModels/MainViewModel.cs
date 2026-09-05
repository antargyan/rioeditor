using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using ReactiveUI;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using RioEditor.Core.Export;
using RioEditor.Core.Models;
using RioEditor.Core.Settings;
using RioEditor.Core.Sponsorship;
using RioEditor.Core.Storage;

namespace RioEditor.App.ViewModels;

/// <summary>
/// Shell view model: owns the document, wires the WebView bridge, drives file I/O, autosave,
/// theme and session restore.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const string DraftKey = "rio.draft";
    private const string DraftNameKey = "rio.draft.name";

    private readonly IWebViewBridge _bridge;
    private readonly IFileService _files;
    private readonly ISettingsService _settings;
    private readonly IEditorSurface _surface;
    private readonly IThemeService _theme;
    private readonly IKeyValueStore _store;
    private readonly IExportService _export;
    private readonly ISponsorPolicy _sponsor;
    private readonly ILinkService _links;
    private readonly IStartupDocument _startup;
    private readonly CompositeDisposable _disposables = new();

    private readonly ObservableAsPropertyHelper<string> _title;
    private readonly ObservableAsPropertyHelper<string> _statusText;

    private string _statusMessage = "Ready";
    private int _wordCount;
    private bool _isSaving;
    private bool _isEditorReady;
    private bool _isCompact;
    private bool _isFileMenuOpen;
    private bool _isSponsorPromptVisible;

    public MainViewModel(
        IWebViewBridge bridge,
        IFileService files,
        ISettingsService settings,
        IEditorSurface surface,
        IThemeService theme,
        IKeyValueStore store,
        IExportService export,
        ISponsorPolicy sponsor,
        ILinkService links,
        IStartupDocument startup)
    {
        _bridge = bridge;
        _files = files;
        _settings = settings;
        _surface = surface;
        _theme = theme;
        _store = store;
        _export = export;
        _sponsor = sponsor;
        _links = links;
        _startup = startup;

        Toolbar = new ToolbarViewModel(bridge);

        // --- derived state -------------------------------------------------
        var documentChanges = Observable
            .FromEvent<PropertyChangedEventHandler, string?>(
                handler => (_, args) => handler(args.PropertyName),
                handler => Document.PropertyChanged += handler,
                handler => Document.PropertyChanged -= handler)
            .StartWith((string?)null);

        _title = documentChanges
            .Select(_ => $"{(Document.IsDirty ? "• " : string.Empty)}{Document.FileName} — RioEditor")
            .ToProperty(this, nameof(Title), $"{Document.FileName} — RioEditor");

        _statusText = this
            .WhenAnyValue(x => x.WordCount, x => x.StatusMessage)
            .Select(pair => $"{pair.Item1:N0} words   ·   {pair.Item2}")
            .ToProperty(this, nameof(StatusText), "0 words   ·   Ready");

        // --- commands ------------------------------------------------------
        NewDocument = ReactiveCommand.CreateFromTask(NewDocumentAsync);
        Open = ReactiveCommand.CreateFromTask(OpenAsync);
        Save = ReactiveCommand.CreateFromTask(() => SaveAsync(promptForPath: false));
        SaveAs = ReactiveCommand.CreateFromTask(() => SaveAsync(promptForPath: true));
        ToggleTheme = ReactiveCommand.CreateFromTask(ToggleThemeAsync);
        ToggleFileMenu = ReactiveCommand.Create(() => { IsFileMenuOpen = !IsFileMenuOpen; });
        ExportHtml = ReactiveCommand.CreateFromTask(ExportHtmlAsync);
        ExportPdf = ReactiveCommand.CreateFromTask(ExportPdfAsync);

        OpenSponsorPage = ReactiveCommand.CreateFromTask(async () =>
        {
            IsSponsorPromptVisible = false;
            await _sponsor.DismissForeverAsync();
            if (!await _links.OpenAsync(_sponsor.SponsorUri))
            {
                StatusMessage = $"Open {_sponsor.SponsorUri} to sponsor";
            }
        });

        // "Later" leaves the counters intact; the quiet period in SponsorPolicy does the rest.
        DismissSponsorPrompt = ReactiveCommand.Create(() => { IsSponsorPromptVisible = false; });

        NeverAskAboutSponsoring = ReactiveCommand.CreateFromTask(async () =>
        {
            IsSponsorPromptVisible = false;
            await _sponsor.DismissForeverAsync();
        });

        // Surface every command failure in the status bar instead of tearing the app down.
        foreach (var command in new IObservable<Exception>[]
                 {
                     NewDocument.ThrownExceptions, Open.ThrownExceptions,
                     Save.ThrownExceptions, SaveAs.ThrownExceptions, ToggleTheme.ThrownExceptions,
                     ExportHtml.ThrownExceptions, ExportPdf.ThrownExceptions
                 })
        {
            _disposables.Add(command.Subscribe(e => StatusMessage = $"Error: {e.Message}"));
        }

        _bridge.Ready += OnEditorReady;
        _bridge.DocumentChanged += OnDocumentChanged;
        _bridge.StatsChanged += OnStatsChanged;
    }

    public DocumentModel Document { get; } = new();

    public ToolbarViewModel Toolbar { get; }

    public IEditorSurface Surface => _surface;

    public string Title => _title.Value;

    public string StatusText => _statusText.Value;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public int WordCount
    {
        get => _wordCount;
        private set => this.RaiseAndSetIfChanged(ref _wordCount, value);
    }

    /// <summary>
    /// True on phone-width layouts. Set by the view from its own size rather than from a device
    /// check, so a narrow desktop window gets the same treatment as a phone.
    /// </summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCompact, value);
            Toolbar.IsCompact = value;
        }
    }

    /// <summary>
    /// Compact-layout file menu. Deliberately an inline panel rather than a Flyout: the editing
    /// surface is a *native* WebView layered above Avalonia's canvas, so any popup that overlaps it
    /// is occluded by it. Anything that must stay visible has to live inside the chrome.
    /// </summary>
    public bool IsFileMenuOpen
    {
        get => _isFileMenuOpen;
        set => this.RaiseAndSetIfChanged(ref _isFileMenuOpen, value);
    }

    /// <summary>
    /// Shown at most three times in the life of an install, and only once the usage thresholds in
    /// <see cref="SponsorPolicy"/> are met. A banner rather than a dialog: it never blocks typing,
    /// and it never interrupts quitting.
    /// </summary>
    public bool IsSponsorPromptVisible
    {
        get => _isSponsorPromptVisible;
        private set => this.RaiseAndSetIfChanged(ref _isSponsorPromptVisible, value);
    }

    public bool IsEditorReady
    {
        get => _isEditorReady;
        private set => this.RaiseAndSetIfChanged(ref _isEditorReady, value);
    }

    public ReactiveCommand<Unit, Unit> NewDocument { get; }

    public ReactiveCommand<Unit, Unit> Open { get; }

    public ReactiveCommand<Unit, Unit> Save { get; }

    public ReactiveCommand<Unit, Unit> SaveAs { get; }

    public ReactiveCommand<Unit, Unit> ToggleTheme { get; }

    public ReactiveCommand<Unit, Unit> ToggleFileMenu { get; }

    public ReactiveCommand<Unit, Unit> ExportHtml { get; }

    public ReactiveCommand<Unit, Unit> ExportPdf { get; }

    public ReactiveCommand<Unit, Unit> OpenSponsorPage { get; }

    public ReactiveCommand<Unit, Unit> DismissSponsorPrompt { get; }

    public ReactiveCommand<Unit, Unit> NeverAskAboutSponsoring { get; }

    /// <summary>
    /// Called by the view once the surface is in the visual tree: loads settings, boots the
    /// WebView, then starts the autosave loop.
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = await _settings.LoadAsync().ConfigureAwait(true);
        await _sponsor.RecordLaunchAsync().ConfigureAwait(true);

        _theme.Apply(settings.Theme);
        Toolbar.IsDarkTheme = settings.Theme == AppTheme.Dark;

        if (!_surface.IsAvailable)
        {
            // The full explanation is several lines and belongs in the diagnostic panel, which is
            // filling the editor area by now; the status bar only has room to agree with it.
            StatusMessage = "No editing surface";
            return;
        }

        await _bridge
            .AttachAsync(_surface.Transport, settings.Theme, settings.Wasm.AllowRemoteScripts)
            .ConfigureAwait(true);

        StartAutosave(settings.AutosaveIntervalSeconds);
    }

    private void StartAutosave(int intervalSeconds)
    {
        if (intervalSeconds <= 0)
        {
            return;
        }

        _disposables.Add(Observable
            .Interval(TimeSpan.FromSeconds(intervalSeconds))
            .ObserveOn(RxApp.MainThreadScheduler)
            // Never overlap autosave passes, and never fight an explicit save.
            .Where(_ => Document.IsDirty && !_isSaving && IsEditorReady)
            .SelectMany(_ => Observable.FromAsync(AutosaveAsync))
            .Subscribe(
                _ => { },
                e => StatusMessage = $"Autosave failed: {e.Message}"));
    }

    // ------------------------------------------------------------------ bridge events

    private void OnEditorReady(object? sender, EventArgs e) =>
        RxApp.MainThreadScheduler.Schedule(() => _ = RestoreSessionSafeAsync());

    /// <summary>
    /// Session restore runs detached from any command pipeline, so a failure here would otherwise
    /// surface as an unobserved task exception and a silently empty editor.
    /// </summary>
    private async Task RestoreSessionSafeAsync()
    {
        try
        {
            await RestoreSessionAsync();
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not restore the last session: {e.Message}";
        }
    }

    private void OnStatsChanged(object? sender, DocumentStatsEventArgs e) =>
        RxApp.MainThreadScheduler.Schedule(() => WordCount = e.WordCount);

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e) =>
        RxApp.MainThreadScheduler.Schedule(() =>
        {
            Document.Html = e.Html;
            Document.Markdown = e.Markdown;
            Document.IsDirty = true;
            WordCount = e.WordCount;
        });

    // ------------------------------------------------------------------ session restore

    private async Task RestoreSessionAsync()
    {
        IsEditorReady = true;
        var settings = _settings.Current;

        // 0. A file named on the command line — which is how Windows hands over a document
        //    opened through the .md association — outranks anything from the previous session.
        if (_startup.Path is { } launchPath && _files.Exists(launchPath))
        {
            var launchText = await _files.ReadAsync(launchPath).ConfigureAwait(true);
            if (launchText is not null)
            {
                Document.FilePath = launchPath;
                await LoadIntoEditorAsync(launchText).ConfigureAwait(true);
                Document.IsDirty = false;

                // Make it the session's document too, so a later plain launch reopens it.
                _settings.Current.LastOpenedFile = launchPath;
                await _settings.SaveAsync().ConfigureAwait(true);

                StatusMessage = $"Opened {Document.FileName}";
                return;
            }
        }

        // 1. A real file from the previous session, when the platform has a file system.
        if (!string.IsNullOrEmpty(settings.LastOpenedFile) && _files.Exists(settings.LastOpenedFile))
        {
            var text = await _files.ReadAsync(settings.LastOpenedFile).ConfigureAwait(true);
            if (text is not null)
            {
                Document.FilePath = settings.LastOpenedFile;
                await LoadIntoEditorAsync(text).ConfigureAwait(true);
                Document.IsDirty = false;
                StatusMessage = $"Restored {Document.FileName}";
                await MaybeShowSponsorPromptAsync().ConfigureAwait(true);
                return;
            }
        }

        // 2. Otherwise an in-browser draft (the WASM equivalent of "last opened file").
        if (settings.Wasm.PersistDraftInBrowserStorage)
        {
            var draft = await _store.GetAsync(DraftKey).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(draft))
            {
                var name = await _store.GetAsync(DraftNameKey).ConfigureAwait(true);
                await LoadIntoEditorAsync(draft).ConfigureAwait(true);
                Document.IsDirty = true;
                StatusMessage = $"Restored unsaved draft{(string.IsNullOrEmpty(name) ? string.Empty : $" ({name})")}";
                await MaybeShowSponsorPromptAsync().ConfigureAwait(true);
                return;
            }
        }

        // 3. First run: a short document that doubles as a feature tour.
        await LoadIntoEditorAsync(WelcomeDocument).ConfigureAwait(true);
        Document.IsDirty = false;
        StatusMessage = "Ready";
        await MaybeShowSponsorPromptAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Evaluated after the document is on screen, so the app has already been useful before it asks
    /// for anything.
    /// </summary>
    private async Task MaybeShowSponsorPromptAsync()
    {
        if (!_sponsor.ShouldPrompt())
        {
            return;
        }

        await _sponsor.RecordPromptShownAsync().ConfigureAwait(true);
        IsSponsorPromptVisible = true;
    }

    private async Task LoadIntoEditorAsync(string markdown)
    {
        Document.Markdown = markdown;
        await _bridge.SetMarkdownAsync(markdown).ConfigureAwait(true);
        Document.IsDirty = false;
    }

    // ------------------------------------------------------------------ commands

    private async Task NewDocumentAsync()
    {
        IsFileMenuOpen = false;
        if (Document.IsDirty)
        {
            await SaveDraftAsync().ConfigureAwait(true);
        }

        Document.Reset();
        await _bridge.SetMarkdownAsync(string.Empty).ConfigureAwait(true);
        await _bridge.FocusAsync().ConfigureAwait(true);
        StatusMessage = "New document";
    }

    private async Task OpenAsync()
    {
        IsFileMenuOpen = false;
        var opened = await _files.OpenAsync().ConfigureAwait(true);
        if (opened is not { } document)
        {
            return;
        }

        Document.FilePath = document.Path;
        await LoadIntoEditorAsync(document.Text).ConfigureAwait(true);

        _settings.Current.LastOpenedFile = document.Path;
        await _settings.SaveAsync().ConfigureAwait(true);

        StatusMessage = $"Opened {document.DisplayName}";
    }

    private async Task SaveAsync(bool promptForPath)
    {
        IsFileMenuOpen = false;
        _isSaving = true;
        try
        {
            // Always pull the freshest Markdown straight from the surface: the debounced
            // docChanged event may still be in flight when the user hits Ctrl+S.
            var markdown = await _bridge.GetMarkdownAsync().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(markdown))
            {
                Document.Markdown = markdown;
            }

            var path = promptForPath
                ? await _files.SaveAsAsync(Document.Markdown, Document.FileName).ConfigureAwait(true)
                : await _files.SaveAsync(Document.Markdown, Document.FilePath).ConfigureAwait(true);

            if (path is null && !_files.SupportsDirectFileAccess)
            {
                // Browser: the download happened but no path exists to remember.
                Document.IsDirty = false;
                StatusMessage = "Downloaded";
                return;
            }

            if (path is null)
            {
                StatusMessage = "Save cancelled";
                return;
            }

            Document.FilePath = path;
            Document.IsDirty = false;
            _settings.Current.LastOpenedFile = path;
            await _settings.SaveAsync().ConfigureAwait(true);
            await _sponsor.RecordSaveAsync().ConfigureAwait(true);
            StatusMessage = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    // ------------------------------------------------------------------ export

    private async Task ExportHtmlAsync()
    {
        IsFileMenuOpen = false;

        var markdown = await CurrentMarkdownAsync().ConfigureAwait(true);
        var settings = _settings.Current;
        var title = Path.GetFileNameWithoutExtension(Document.FileName);

        var html = _export.BuildStandaloneHtml(markdown, title, settings.Theme,
            settings.Wasm.AllowRemoteScripts);

        var path = await _files.SaveExportAsync(
            System.Text.Encoding.UTF8.GetBytes(html),
            $"{title}.html", "html", "HTML document", "text/html").ConfigureAwait(true);

        StatusMessage = path is null
            ? _files.SupportsDirectFileAccess ? "Export cancelled" : "Downloaded HTML"
            : $"Exported {Path.GetFileName(path)}";
    }

    /// <summary>
    /// Three tiers, best first. The WebView already lays this document out, so using its own
    /// renderer is what makes the PDF match the screen — far better than re-flowing Markdown
    /// through a separate PDF library and hoping the two agree.
    /// </summary>
    private async Task ExportPdfAsync()
    {
        IsFileMenuOpen = false;

        var exporter = _surface as IPdfExporter;

        // 1. Native PDF bytes (WKWebView on macOS and iOS).
        if (exporter is { CanProducePdfBytes: true })
        {
            var bytes = await exporter.ExportPdfBytesAsync().ConfigureAwait(true);
            if (bytes is { Length: > 0 })
            {
                var name = Path.GetFileNameWithoutExtension(Document.FileName);
                var path = await _files.SaveExportAsync(bytes, $"{name}.pdf", "pdf",
                    "PDF document", "application/pdf").ConfigureAwait(true);

                StatusMessage = path is null
                    ? _files.SupportsDirectFileAccess ? "Export cancelled" : "Downloaded PDF"
                    : $"Exported {Path.GetFileName(path)}";
                return;
            }

            StatusMessage = "PDF export failed; falling back to the print dialog";
        }

        // 2. The platform's own print UI, which offers "Save as PDF" (Android).
        if (exporter is not null && await exporter.TryShowPrintUiAsync().ConfigureAwait(true))
        {
            StatusMessage = "Opened the system print dialog";
            return;
        }

        // 3. window.print() inside the document (browsers, and desktop WebViews with no print API).
        await _bridge.PrintAsync().ConfigureAwait(true);
        StatusMessage = "Opened the print dialog — choose \"Save as PDF\"";
    }

    /// <summary>Freshest Markdown, straight from the surface, falling back to the cached buffer.</summary>
    private async Task<string> CurrentMarkdownAsync()
    {
        var markdown = await _bridge.GetMarkdownAsync().ConfigureAwait(true);
        return string.IsNullOrEmpty(markdown) ? Document.Markdown : markdown;
    }

    private async Task AutosaveAsync()
    {
        _isSaving = true;
        try
        {
            var markdown = await _bridge.GetMarkdownAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(markdown))
            {
                return;
            }

            Document.Markdown = markdown;

            if (!string.IsNullOrEmpty(Document.FilePath) &&
                await _files.WriteAsync(Document.FilePath, markdown).ConfigureAwait(true))
            {
                Document.IsDirty = false;
                StatusMessage = $"Autosaved {DateTime.Now:HH:mm:ss}";
                return;
            }

            // No writable path (unsaved buffer, or WASM): keep a draft instead of losing work.
            await SaveDraftAsync().ConfigureAwait(true);
            StatusMessage = $"Draft kept {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task SaveDraftAsync()
    {
        if (!_settings.Current.Wasm.PersistDraftInBrowserStorage)
        {
            return;
        }

        await _store.SetAsync(DraftKey, Document.Markdown).ConfigureAwait(true);
        await _store.SetAsync(DraftNameKey, Document.FileName).ConfigureAwait(true);
    }

    private async Task ToggleThemeAsync()
    {
        var next = _settings.Current.Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;

        _settings.Current.Theme = next;
        _theme.Apply(next);
        Toolbar.IsDarkTheme = next == AppTheme.Dark;

        await _bridge.SetThemeAsync(next).ConfigureAwait(true);
        await _settings.SaveAsync().ConfigureAwait(true);
    }

    /// <summary>Persists window geometry. Called by the window on close.</summary>
    public async Task PersistWindowStateAsync(double width, double height, bool maximized)
    {
        _settings.Current.WindowWidth = width;
        _settings.Current.WindowHeight = height;
        _settings.Current.WindowMaximized = maximized;
        await _settings.SaveAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _bridge.Ready -= OnEditorReady;
        _bridge.DocumentChanged -= OnDocumentChanged;
        _bridge.StatsChanged -= OnStatsChanged;
        _disposables.Dispose();
        _title.Dispose();
        _statusText.Dispose();
    }

    private const string WelcomeDocument = """
        # Welcome to RioEditor

        A **Typora-style** WYSIWYG Markdown editor: there is no split view and no preview pane —
        the rendered document *is* the thing you type into.

        ## Try it

        - Type `**bold**` and watch the asterisks disappear the moment you close them
        - Start a line with `# ` for a heading, `- ` for a bullet, `> ` for a quote
        - Type ``` then press Enter for a code block
        - Press `Cmd/Ctrl+B`, `Cmd/Ctrl+I`, `Cmd/Ctrl+K`

        - [ ] Task lists work too
        - [x] Including checked ones

        ```csharp
        // Fenced code blocks are highlighted in place.
        public static string Greet(string name) => $"Hello, {name}!";
        ```

        | Feature | Status |
        | --- | :---: |
        | Tables | ✅ |
        | Footnotes | ✅ |
        | Math | ✅ |

        Inline math renders with KaTeX: $e^{i\pi} + 1 = 0$

        ```mermaid
        graph LR
          A[Markdown] --> B(Markdig)
          B --> C{Sanitizer}
          C --> D[WebView]
          D --> A
        ```

        > Autosave runs every five seconds; the dot in the title bar tells you when
        > there is something to save.
        """;
}

/// <summary>Minimal composite disposable so the view model does not need System.Reactive.Disposables sugar.</summary>
internal sealed class CompositeDisposable : IDisposable
{
    private readonly List<IDisposable> _items = [];

    public void Add(IDisposable item) => _items.Add(item);

    public void Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }

        _items.Clear();
    }
}
