using System.ComponentModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using ReactiveUI;
using RioEditor.App.Services;
using RioEditor.Core.Editor;
using RioEditor.Core.Models;
using RioEditor.Core.Settings;
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
    private readonly CompositeDisposable _disposables = new();

    private readonly ObservableAsPropertyHelper<string> _title;
    private readonly ObservableAsPropertyHelper<string> _statusText;

    private string _statusMessage = "Ready";
    private int _wordCount;
    private bool _isSaving;
    private bool _isEditorReady;

    public MainViewModel(
        IWebViewBridge bridge,
        IFileService files,
        ISettingsService settings,
        IEditorSurface surface,
        IThemeService theme,
        IKeyValueStore store)
    {
        _bridge = bridge;
        _files = files;
        _settings = settings;
        _surface = surface;
        _theme = theme;
        _store = store;

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

        // Surface every command failure in the status bar instead of tearing the app down.
        foreach (var command in new IObservable<Exception>[]
                 {
                     NewDocument.ThrownExceptions, Open.ThrownExceptions,
                     Save.ThrownExceptions, SaveAs.ThrownExceptions, ToggleTheme.ThrownExceptions
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

    /// <summary>
    /// Called by the view once the surface is in the visual tree: loads settings, boots the
    /// WebView, then starts the autosave loop.
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = await _settings.LoadAsync().ConfigureAwait(true);

        _theme.Apply(settings.Theme);
        Toolbar.IsDarkTheme = settings.Theme == AppTheme.Dark;

        if (!_surface.IsAvailable)
        {
            StatusMessage = _surface.UnavailableReason ?? "No WebView available on this platform.";
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
                return;
            }
        }

        // 3. First run: a short document that doubles as a feature tour.
        await LoadIntoEditorAsync(WelcomeDocument).ConfigureAwait(true);
        Document.IsDirty = false;
        StatusMessage = "Ready";
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
            StatusMessage = $"Saved {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _isSaving = false;
        }
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
