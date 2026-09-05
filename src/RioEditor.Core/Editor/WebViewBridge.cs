using System.Collections.Concurrent;
using System.Text.Json;
using RioEditor.Core.Markdown;
using RioEditor.Core.Models;
using RioEditor.Core.Sanitization;

namespace RioEditor.Core.Editor;

/// <summary>
/// Implements the host half of the editor protocol.
///
/// Message flow (all JSON, both directions):
///   host -> engine : window.rio.receive('{"type":"..."}')
///   engine -> host : postToHost('{"type":"..."}')  (platform shim inside editor.js)
///
/// The pipeline is deliberately *incremental*. A naive "re-render the whole document on every
/// keystroke" loop destroys the caret and makes typing feel laggy. Instead:
///   * inline formatting is applied in the DOM by the engine (instant, caret never moves);
///   * when the caret leaves a block, the engine asks the host to re-render just that block;
///   * a full render only happens on load / SetMarkdown.
/// </summary>
public sealed class WebViewBridge : IWebViewBridge
{
    private readonly IMarkdownService _markdown;
    private readonly IHtmlToMarkdownService _htmlToMarkdown;
    private readonly IHtmlSanitizer _sanitizer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    private IWebViewTransport? _transport;

    public WebViewBridge(
        IMarkdownService markdown,
        IHtmlToMarkdownService htmlToMarkdown,
        IHtmlSanitizer sanitizer)
    {
        _markdown = markdown;
        _htmlToMarkdown = htmlToMarkdown;
        _sanitizer = sanitizer;
    }

    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<DocumentChangedEventArgs>? DocumentChanged;

    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

    public async Task AttachAsync(IWebViewTransport transport, AppTheme theme, bool allowRemoteScripts)
    {
        if (_transport is not null)
        {
            _transport.MessageReceived -= OnMessageReceived;
        }

        _transport = transport;
        _transport.MessageReceived += OnMessageReceived;
        IsReady = false;

        await transport.LoadHtmlAsync(EditorDocumentFactory.Build(theme, allowRemoteScripts))
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- host -> engine

    public Task SetMarkdownAsync(string markdown, CancellationToken cancellationToken = default)
    {
        // Markdig + sanitizer run host-side, so the engine only ever receives trusted HTML.
        var html = _markdown.ToHtml(markdown);
        return SendAsync(new HostMessage { Type = "setHtml", Html = html, Markdown = markdown }, cancellationToken);
    }

    public async Task<string> GetMarkdownAsync(CancellationToken cancellationToken = default)
    {
        var html = await RequestAsync("getHtml", cancellationToken).ConfigureAwait(false);
        return _htmlToMarkdown.ToMarkdown(html);
    }

    public Task ApplyBoldAsync() => CommandAsync("bold");

    public Task ApplyItalicAsync() => CommandAsync("italic");

    public Task ApplyStrikethroughAsync() => CommandAsync("strikethrough");

    public Task ApplyInlineCodeAsync() => CommandAsync("inlineCode");

    public Task ApplyHeadingAsync(int level) =>
        CommandAsync("heading", Math.Clamp(level, 0, 6));

    public Task ApplyLinkAsync(string url, string? text = null) =>
        SendAsync(new HostMessage { Type = "command", Name = "link", Url = url, Text = text });

    public Task ApplyCodeBlockAsync(string language = "") =>
        SendAsync(new HostMessage { Type = "command", Name = "codeBlock", Language = language });

    public Task ApplyQuoteAsync() => CommandAsync("quote");

    public Task ApplyBulletListAsync() => CommandAsync("bulletList");

    public Task ApplyOrderedListAsync() => CommandAsync("orderedList");

    public Task ApplyTaskListAsync() => CommandAsync("taskList");

    public Task InsertTableAsync(int rows = 3, int columns = 3) =>
        SendAsync(new HostMessage { Type = "command", Name = "table", Rows = rows, Columns = columns });

    public Task ApplyHorizontalRuleAsync() => CommandAsync("horizontalRule");

    public Task ToggleThemeAsync() => SendAsync(new HostMessage { Type = "toggleTheme" });

    public Task SetThemeAsync(AppTheme theme) =>
        SendAsync(new HostMessage { Type = "setTheme", Theme = theme == AppTheme.Dark ? "dark" : "light" });

    public Task FocusAsync() => SendAsync(new HostMessage { Type = "focus" });

    // ---------------------------------------------------------------- engine -> host

    private void OnMessageReceived(object? sender, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "ready":
                    IsReady = true;
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;

                case "docChanged":
                {
                    var html = root.GetPropertyOrEmpty("html");
                    var words = root.TryGetProperty("wordCount", out var w) ? w.GetInt32() : 0;
                    // The reverse pipeline runs here, off the engine's hot path.
                    var markdown = _htmlToMarkdown.ToMarkdown(html);
                    DocumentChanged?.Invoke(this, new DocumentChangedEventArgs(html, markdown, words));
                    break;
                }

                case "renderBlock":
                {
                    // Incremental render. The engine sends the block's *HTML*; we round-trip it
                    // through the reverse pipeline and back so the block ends up in canonical form
                    // (raw "**bold**" the user typed becomes <strong>, a "# " prefix becomes <h1>).
                    var requestId = root.GetPropertyOrEmpty("requestId");
                    var source = root.GetPropertyOrEmpty("html");
                    var blockMarkdown = _htmlToMarkdown.ToMarkdown(source);
                    var html = _markdown.ToHtmlBlock(blockMarkdown);
                    _ = SendAsync(new HostMessage { Type = "blockRendered", RequestId = requestId, Html = html });
                    break;
                }

                case "hostRequest":
                {
                    // Serves the engine's public promise-based API (getMarkdown / setMarkdown), so
                    // there is exactly one Markdown implementation in the app and it lives in C#.
                    var requestId = root.GetPropertyOrEmpty("requestId");
                    var value = root.GetPropertyOrEmpty("request") switch
                    {
                        "markdown" => _htmlToMarkdown.ToMarkdown(root.GetPropertyOrEmpty("html")),
                        "render" => _markdown.ToHtml(root.GetPropertyOrEmpty("markdown")),
                        _ => string.Empty
                    };

                    _ = SendAsync(new HostMessage { Type = "hostResponse", RequestId = requestId, Value = value });
                    break;
                }

                case "sanitize":
                {
                    // Paste path: untrusted clipboard HTML gets whitelisted before it lands.
                    var requestId = root.GetPropertyOrEmpty("requestId");
                    var dirty = root.GetPropertyOrEmpty("html");
                    var markdown = _htmlToMarkdown.ToMarkdown(_sanitizer.Sanitize(dirty));
                    var html = _markdown.ToHtml(markdown);
                    _ = SendAsync(new HostMessage { Type = "sanitized", RequestId = requestId, Html = html });
                    break;
                }

                case "response":
                {
                    var requestId = root.GetPropertyOrEmpty("requestId");
                    if (_pending.TryRemove(requestId, out var completion))
                    {
                        completion.TrySetResult(root.GetPropertyOrEmpty("value"));
                    }

                    break;
                }

                case "selection":
                    SelectionChanged?.Invoke(this, new SelectionChangedEventArgs
                    {
                        Bold = root.GetBoolOrDefault("bold"),
                        Italic = root.GetBoolOrDefault("italic"),
                        InlineCode = root.GetBoolOrDefault("inlineCode"),
                        HeadingLevel = root.TryGetProperty("headingLevel", out var h) ? h.GetInt32() : 0,
                        BlockType = root.GetPropertyOrEmpty("blockType")
                    });
                    break;
            }
        }
        catch (JsonException)
        {
            // A malformed message must never take the editor down.
        }
    }

    // ---------------------------------------------------------------- plumbing

    private Task CommandAsync(string name, int? value = null) =>
        SendAsync(new HostMessage { Type = "command", Name = name, Level = value });

    private async Task<string> RequestAsync(string request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
            }
        });

        await SendAsync(new HostMessage { Type = "request", Request = request, RequestId = requestId }, cancellationToken).ConfigureAwait(false);

        // The WebView can be slow to come up; a bounded wait beats hanging a save.
        var timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        var winner = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
        if (winner != completion.Task)
        {
            _pending.TryRemove(requestId, out _);
            return string.Empty;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private Task SendAsync(HostMessage message, CancellationToken cancellationToken = default)
    {
        if (_transport is null)
        {
            return Task.CompletedTask;
        }

        // Serializing to a JSON string literal is what makes this injection-proof: the payload is
        // never concatenated into an executable position, only into a single string argument.
        var json = JsonSerializer.Serialize(message, EditorJsonContext.Default.HostMessage);
        var literal = JsonSerializer.Serialize(json, EditorJsonContext.Default.String);
        return _transport.ExecuteScriptAsync($"window.rio && window.rio.receive({literal});", cancellationToken);
    }
}

internal static class JsonElementExtensions
{
    public static string GetPropertyOrEmpty(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public static bool GetBoolOrDefault(this JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True;
}
