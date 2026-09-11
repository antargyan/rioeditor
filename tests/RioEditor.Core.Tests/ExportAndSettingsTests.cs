using RioEditor.Core.Models;
using RioEditor.Core.Settings;
using RioEditor.Core.Storage;
using Xunit;

namespace RioEditor.Core.Tests;

public class ExportTests
{
    [Fact]
    public void Exported_html_is_self_contained()
    {
        var html = TestPipeline.Export.BuildStandaloneHtml(
            "# Title\n\nBody.", "Title", AppTheme.Light, allowRemoteScripts: false);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("Title", html);
        Assert.Contains("Body.", html);
    }

    [Fact]
    public void Export_with_remote_scripts_disabled_references_no_external_host()
    {
        // A reader who disabled remote scripts has asked for a file that touches nothing.
        var html = TestPipeline.Export.BuildStandaloneHtml(
            "Math $x$ and a diagram.", "T", AppTheme.Light, allowRemoteScripts: false);

        Assert.DoesNotContain("<script src=\"http", html);
        Assert.DoesNotContain("<link rel=\"stylesheet\" href=\"http", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html);
    }

    [Fact]
    public void Export_with_remote_scripts_enabled_pulls_in_the_renderers()
    {
        var html = TestPipeline.Export.BuildStandaloneHtml(
            "Math $x$.", "T", AppTheme.Light, allowRemoteScripts: true);

        Assert.Contains("katex", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mermaid", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_carries_print_rules()
    {
        var html = TestPipeline.Export.BuildStandaloneHtml("# T", "T", AppTheme.Light, false);

        Assert.Contains("@page", html);
        Assert.Contains("break-inside", html);
    }

    [Fact]
    public void Export_is_sanitized()
    {
        var html = TestPipeline.Export.BuildStandaloneHtml(
            "Before\n\n<script>alert(1)</script>\n\nAfter", "T", AppTheme.Light, false);

        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains("Before", html);
        Assert.Contains("After", html);
    }

    [Fact]
    public void Export_escapes_the_title_it_is_given()
    {
        var html = TestPipeline.Export.BuildStandaloneHtml(
            "body", "</title><script>alert(1)</script>", AppTheme.Light, false);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Theory]
    [InlineData(AppTheme.Light, "light")]
    [InlineData(AppTheme.Dark, "dark")]
    public void Export_records_the_theme(AppTheme theme, string expected)
    {
        var html = TestPipeline.Export.BuildStandaloneHtml("x", "t", theme, false);

        Assert.Contains($"data-theme=\"{expected}\"", html);
    }
}

public class SettingsTests
{
    private sealed class MemoryStore : IKeyValueStore
    {
        public Dictionary<string, string> Map { get; } = new();

        public ValueTask<string?> GetAsync(string k, CancellationToken ct = default) =>
            ValueTask.FromResult(Map.GetValueOrDefault(k));

        public ValueTask SetAsync(string k, string v, CancellationToken ct = default)
        {
            Map[k] = v;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string k, CancellationToken ct = default)
        {
            Map.Remove(k);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Settings_survive_a_save_and_reload()
    {
        var store = new MemoryStore();

        var first = new SettingsService(store);
        await first.LoadAsync();
        first.Current.Theme = AppTheme.Dark;
        first.Current.LastOpenedFile = "/tmp/doc.md";
        first.Current.Sponsor.LaunchCount = 7;
        await first.SaveAsync();

        var second = new SettingsService(store);
        var loaded = await second.LoadAsync();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("/tmp/doc.md", loaded.LastOpenedFile);
        Assert.Equal(7, loaded.Sponsor.LaunchCount);
    }

    [Fact]
    public async Task A_corrupt_settings_blob_does_not_stop_the_app_starting()
    {
        // Losing preferences is an inconvenience; refusing to launch is a catastrophe.
        var store = new MemoryStore();
        await store.SetAsync("rio.settings", "{ this is not json");

        var settings = new SettingsService(store);
        var loaded = await settings.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(AppTheme.Light, loaded.Theme);
    }

    [Fact]
    public async Task Defaults_are_sensible_on_a_first_run()
    {
        var settings = new SettingsService(new MemoryStore());
        var loaded = await settings.LoadAsync();

        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.Null(loaded.LastOpenedFile);
        Assert.True(loaded.AutosaveIntervalSeconds > 0);
        Assert.False(loaded.Sponsor.Dismissed);
        Assert.Equal(0, loaded.Sponsor.LaunchCount);
    }
}

public class DocumentModelTests
{
    [Fact]
    public void File_name_falls_back_when_there_is_no_path()
    {
        var document = new DocumentModel();

        Assert.Equal("Untitled.md", document.FileName);
    }

    [Fact]
    public void File_name_follows_the_path()
    {
        var document = new DocumentModel { FilePath = "/a/b/Notes.md" };

        Assert.Equal("Notes.md", document.FileName);
    }

    [Fact]
    public void Changing_the_path_notifies_that_the_name_changed()
    {
        // The window title binds to FileName, so a silent change leaves a stale title.
        var document = new DocumentModel();
        var changed = new List<string?>();
        document.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        document.FilePath = "/a/Report.md";

        Assert.Contains(nameof(DocumentModel.FileName), changed);
    }

    [Fact]
    public void Reset_clears_everything()
    {
        var document = new DocumentModel
        {
            Markdown = "text", Html = "<p>text</p>", FilePath = "/a.md", IsDirty = true
        };

        document.Reset();

        Assert.Equal(string.Empty, document.Markdown);
        Assert.Null(document.FilePath);
        Assert.False(document.IsDirty);
    }
}
