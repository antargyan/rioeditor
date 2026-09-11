using System.Text.Json;
using RioEditor.Core.Editor;
using Xunit;

namespace RioEditor.Core.Tests;

/// <summary>
/// The host and the engine agree on a JSON shape that neither compiler checks. Renaming a field on
/// one side leaves the other reading <c>undefined</c>, and the symptom is a toolbar button that
/// silently does nothing — no error, no log. These tests pin the wire format by capturing what the
/// bridge actually sends, so the break happens here instead of in someone's hands.
///
/// This is not hypothetical: a test written against the older shape (<c>value</c> rather than
/// <c>level</c>) produced exactly that silent no-op.
/// </summary>
public class HostMessageContractTests
{
    private sealed class CapturingTransport : IWebViewTransport
    {
        public List<string> Scripts { get; } = new();

        public Task LoadHtmlAsync(string html, CancellationToken ct = default) => Task.CompletedTask;

        public Task ExecuteScriptAsync(string script, CancellationToken ct = default)
        {
            Scripts.Add(script);
            return Task.CompletedTask;
        }

        public event EventHandler<string>? MessageReceived;

        public void Receive(string payload) => MessageReceived?.Invoke(this, payload);
    }

    private static (WebViewBridge bridge, CapturingTransport transport) NewBridge()
    {
        var bridge = new WebViewBridge(TestPipeline.Markdown, TestPipeline.Reverse, TestPipeline.Sanitizer);
        var transport = new CapturingTransport();
        bridge.AttachAsync(transport, Models.AppTheme.Light, allowRemoteScripts: false).GetAwaiter().GetResult();
        return (bridge, transport);
    }

    /// <summary>Digs the JSON argument back out of <c>window.rio.receive("…")</c>.</summary>
    private static JsonElement LastMessage(CapturingTransport transport)
    {
        var script = transport.Scripts[^1];
        var open = script.IndexOf('(') + 1;
        var close = script.LastIndexOf(')');
        var literal = script[open..close];
        var json = JsonSerializer.Deserialize<string>(literal)!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task Heading_is_sent_as_level_which_is_what_the_engine_reads()
    {
        var (bridge, transport) = NewBridge();

        await bridge.ApplyHeadingAsync(3);

        var message = LastMessage(transport);
        Assert.Equal("command", message.GetProperty("type").GetString());
        Assert.Equal("heading", message.GetProperty("name").GetString());
        Assert.Equal(3, message.GetProperty("level").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-2)]
    public async Task Heading_levels_are_clamped_to_what_html_has(int requested)
    {
        var (bridge, transport) = NewBridge();

        await bridge.ApplyHeadingAsync(requested);

        var level = LastMessage(transport).GetProperty("level").GetInt32();
        Assert.InRange(level, 0, 6);
    }

    [Theory]
    [InlineData("bold")]
    [InlineData("italic")]
    [InlineData("strikethrough")]
    [InlineData("inlineCode")]
    [InlineData("quote")]
    [InlineData("bulletList")]
    [InlineData("orderedList")]
    [InlineData("taskList")]
    [InlineData("horizontalRule")]
    public async Task Simple_commands_use_the_names_the_engine_dispatches_on(string expected)
    {
        var (bridge, transport) = NewBridge();

        Task task = expected switch
        {
            "bold" => bridge.ApplyBoldAsync(),
            "italic" => bridge.ApplyItalicAsync(),
            "strikethrough" => bridge.ApplyStrikethroughAsync(),
            "inlineCode" => bridge.ApplyInlineCodeAsync(),
            "quote" => bridge.ApplyQuoteAsync(),
            "bulletList" => bridge.ApplyBulletListAsync(),
            "orderedList" => bridge.ApplyOrderedListAsync(),
            "taskList" => bridge.ApplyTaskListAsync(),
            _ => bridge.ApplyHorizontalRuleAsync(),
        };
        await task;

        var message = LastMessage(transport);
        Assert.Equal("command", message.GetProperty("type").GetString());
        Assert.Equal(expected, message.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Link_carries_its_url()
    {
        var (bridge, transport) = NewBridge();

        await bridge.ApplyLinkAsync("https://example.com", "text");

        var message = LastMessage(transport);
        Assert.Equal("link", message.GetProperty("name").GetString());
        Assert.Equal("https://example.com", message.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Table_carries_rows_and_columns()
    {
        var (bridge, transport) = NewBridge();

        await bridge.InsertTableAsync(3, 4);

        var message = LastMessage(transport);
        Assert.Equal("table", message.GetProperty("name").GetString());
        Assert.Equal(3, message.GetProperty("rows").GetInt32());
        Assert.Equal(4, message.GetProperty("columns").GetInt32());
    }

    [Fact]
    public async Task Theme_is_sent_as_the_lowercase_word_the_engine_compares_against()
    {
        var (bridge, transport) = NewBridge();

        await bridge.SetThemeAsync(Models.AppTheme.Dark);

        Assert.Equal("dark", LastMessage(transport).GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Setting_markdown_sends_rendered_html_not_the_markdown()
    {
        // The engine mounts what it is given verbatim, so the host must render and sanitize first.
        var (bridge, transport) = NewBridge();

        await bridge.SetMarkdownAsync("# Title");

        var message = LastMessage(transport);
        Assert.Equal("setHtml", message.GetProperty("type").GetString());
        Assert.Contains("<h1", message.GetProperty("html").GetString());
    }

    [Fact]
    public async Task Payloads_are_passed_as_a_string_argument_so_content_cannot_become_code()
    {
        // The document is attacker-influenced. It reaches the engine as a single JSON string
        // argument; anything else would be script injection with extra steps.
        var (bridge, transport) = NewBridge();

        await bridge.SetMarkdownAsync("</script><script>alert(1)</script>");

        var script = transport.Scripts[^1];
        Assert.StartsWith("window.rio && window.rio.receive(\"", script);
        Assert.DoesNotContain("<script>alert(1)</script>", script);
    }

    [Fact]
    public void A_document_change_from_the_engine_is_converted_back_to_markdown()
    {
        var (bridge, transport) = NewBridge();
        DocumentChangedEventArgs? seen = null;
        bridge.DocumentChanged += (_, e) => seen = e;

        transport.Receive("""{"type":"docChanged","html":"<h1>Heading</h1>","wordCount":1}""");

        Assert.NotNull(seen);
        Assert.Contains("# Heading", seen!.Markdown);
        Assert.Equal(1, seen.WordCount);
    }

    [Fact]
    public void A_malformed_engine_message_is_ignored_rather_than_thrown()
    {
        var (bridge, transport) = NewBridge();

        var exception = Record.Exception(() =>
        {
            transport.Receive("not json at all");
            transport.Receive("{}");
            transport.Receive("""{"type":"unknown"}""");
        });

        Assert.Null(exception);
    }
}
