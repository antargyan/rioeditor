using Xunit;

namespace RioEditor.Core.Tests;

/// <summary>
/// The sanitizer is a security boundary, not a formatter: everything pasted from the clipboard and
/// every scrap of raw HTML inside a Markdown document passes through it before it is mounted in a
/// WebView that can reach the host bridge. These tests are written as attacks.
/// </summary>
public class SanitizerTests
{
    private static string Clean(string html) => TestPipeline.Sanitizer.Sanitize(html);

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT>alert(1)</SCRIPT>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    [InlineData("<object data=\"x.swf\"></object>")]
    [InlineData("<embed src=\"x.swf\">")]
    [InlineData("<form action=\"/x\"><input type=\"text\"></form>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [InlineData("<base href=\"https://evil.example/\">")]
    public void Dangerous_elements_are_removed(string html)
    {
        var clean = Clean($"<p>before</p>{html}<p>after</p>");

        Assert.DoesNotContain("alert(1)", clean);
        Assert.DoesNotContain("evil.example", clean);
        foreach (var tag in new[] { "<script", "<iframe", "<object", "<embed", "<form", "<style", "<link", "<meta", "<base" })
        {
            Assert.DoesNotContain(tag, clean, StringComparison.OrdinalIgnoreCase);
        }

        // Surrounding content must survive: sanitising is not an excuse to discard the document.
        Assert.Contains("before", clean);
        Assert.Contains("after", clean);
    }

    [Theory]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">")]
    [InlineData("<div onclick=\"alert(1)\">text</div>")]
    [InlineData("<body onload=\"alert(1)\">text</body>")]
    [InlineData("<p ONMOUSEOVER=\"alert(1)\">text</p>")]
    [InlineData("<a href=\"#\" onfocus=\"alert(1)\">link</a>")]
    public void Event_handler_attributes_are_stripped(string html)
    {
        var clean = Clean(html);

        Assert.DoesNotContain("alert(1)", clean);
        Assert.DoesNotContain("onerror", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onmouseover", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onfocus", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("file:///etc/passwd")]
    public void Unsafe_url_schemes_are_rejected(string url)
    {
        var clean = Clean($"<a href=\"{url}\">click</a>");

        Assert.DoesNotContain("javascript", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", clean, StringComparison.OrdinalIgnoreCase);

        // The link text is still the user's content and must be kept.
        Assert.Contains("click", clean);
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com/page")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("#anchor")]
    [InlineData("/relative/path")]
    [InlineData("./sibling.md")]
    public void Safe_urls_are_preserved(string url)
    {
        var clean = Clean($"<a href=\"{url}\">click</a>");

        Assert.Contains(url, clean);
    }

    [Fact]
    public void Inline_images_are_allowed_because_that_is_how_pasted_screenshots_arrive()
    {
        const string png = "data:image/png;base64,iVBORw0KGgo=";

        var clean = Clean($"<img src=\"{png}\" alt=\"shot\">");

        Assert.Contains(png, clean);
    }

    [Fact]
    public void Formatting_is_left_intact()
    {
        const string html =
            "<h2>Title</h2><p><strong>bold</strong> <em>italic</em> <code>code</code></p>" +
            "<ul><li>item</li></ul><blockquote><p>quote</p></blockquote>" +
            "<table><tr><th>h</th><td>d</td></tr></table>";

        var clean = Clean(html);

        foreach (var expected in new[] { "<h2>", "<strong>", "<em>", "<code>", "<ul>", "<li>", "<blockquote>", "<table>", "<th>", "<td>" })
        {
            Assert.Contains(expected, clean);
        }
    }

    [Fact]
    public void Unknown_elements_are_unwrapped_but_their_text_is_kept()
    {
        var clean = Clean("<p>a <bogus>kept</bogus> b</p>");

        Assert.DoesNotContain("<bogus", clean);
        Assert.Contains("kept", clean);
    }

    [Fact]
    public void Task_list_checkboxes_survive_but_other_inputs_do_not()
    {
        var clean = Clean("<li><input type=\"checkbox\" checked> done</li><input type=\"password\" name=\"p\">");

        Assert.Contains("checkbox", clean);
        Assert.DoesNotContain("password", clean);
    }

    [Fact]
    public void Anchors_that_open_a_new_window_are_hardened_against_reverse_tabnabbing()
    {
        var clean = Clean("<a href=\"https://example.com\" target=\"_blank\">x</a>");

        Assert.Contains("noopener", clean);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not html at all")]
    [InlineData("<p>unclosed")]
    [InlineData("<<>><p>weird</p>")]
    public void Malformed_input_does_not_throw(string html)
    {
        var exception = Record.Exception(() => Clean(html));
        Assert.Null(exception);
    }

    [Fact]
    public void Markdown_rendering_applies_the_sanitizer()
    {
        // Raw HTML is legal in Markdown, so the forward pipeline must sanitize its own output
        // rather than trusting the author.
        var html = TestPipeline.Markdown.ToHtml("Text\n\n<script>alert(1)</script>\n\nMore");

        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains("Text", html);
    }
}
