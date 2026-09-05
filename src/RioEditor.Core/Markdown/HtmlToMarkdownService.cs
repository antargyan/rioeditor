using System.Globalization;
using System.Text;
using HtmlAgilityPack;

namespace RioEditor.Core.Markdown;

/// <summary>
/// Recursive HTML -> Markdown serializer built on HtmlAgilityPack.
/// Covers everything the forward pipeline can emit: headings, emphasis, code (inline and fenced),
/// links, images, lists (incl. task lists), blockquotes, rules, tables, Mermaid and math blocks.
/// Anything unrecognised degrades to its inner text rather than being dropped.
/// </summary>
public sealed class HtmlToMarkdownService : IHtmlToMarkdownService
{
    public string ToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);

        var builder = new StringBuilder();
        WriteBlocks(document.DocumentNode, builder, new ListContext());

        // Collapse the runs of blank lines that block joins inevitably produce.
        var text = builder.ToString().Replace("\r\n", "\n");
        while (text.Contains("\n\n\n"))
        {
            text = text.Replace("\n\n\n", "\n\n");
        }

        return text.Trim() + "\n";
    }

    /// <summary>Tracks nesting/numbering while walking list structures.</summary>
    private sealed record ListContext(int Depth = 0, bool Ordered = false, int Index = 1)
    {
        public string Indent => new(' ', Depth * 2);
    }

    private static void WriteBlocks(HtmlNode parent, StringBuilder output, ListContext context)
    {
        foreach (var node in parent.ChildNodes)
        {
            WriteBlock(node, output, context);
        }
    }

    private static void WriteBlock(HtmlNode node, StringBuilder output, ListContext context)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
            {
                var text = Normalize(node.InnerText);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    output.Append(text).Append("\n\n");
                }

                return;
            }

            case HtmlNodeType.Comment:
                return;
        }

        switch (node.Name.ToLowerInvariant())
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                var level = int.Parse(node.Name[1..], CultureInfo.InvariantCulture);
                output.Append(new string('#', level)).Append(' ')
                      .Append(Inline(node).Trim()).Append("\n\n");
                return;
            }

            case "p":
            {
                var inline = Inline(node).Trim();
                if (inline.Length > 0)
                {
                    output.Append(context.Indent).Append(inline).Append("\n\n");
                }

                return;
            }

            case "br":
                output.Append("  \n");
                return;

            case "hr":
                output.Append("---\n\n");
                return;

            case "pre":
            {
                var code = node.SelectSingleNode(".//code") ?? node;
                var language = LanguageOf(code);
                var body = HtmlEntity.DeEntitize(code.InnerText).TrimEnd('\n');
                output.Append("```").Append(language).Append('\n')
                      .Append(body).Append("\n```\n\n");
                return;
            }

            case "blockquote":
            {
                var inner = new StringBuilder();
                WriteBlocks(node, inner, context);
                foreach (var line in inner.ToString().TrimEnd().Split('\n'))
                {
                    output.Append("> ").Append(line).Append('\n');
                }

                output.Append('\n');
                return;
            }

            case "ul" or "ol":
            {
                var ordered = node.Name.Equals("ol", StringComparison.OrdinalIgnoreCase);
                var start = ordered
                    ? node.GetAttributeValue("start", 1)
                    : 1;

                var index = start;
                foreach (var item in node.ChildNodes.Where(n =>
                             n.Name.Equals("li", StringComparison.OrdinalIgnoreCase)))
                {
                    WriteListItem(item, output, context with
                    {
                        Ordered = ordered,
                        Index = index
                    });
                    index++;
                }

                if (context.Depth == 0)
                {
                    output.Append('\n');
                }

                return;
            }

            case "table":
                WriteTable(node, output);
                return;

            case "div" when IsMermaid(node):
            {
                // Once Mermaid has drawn the diagram the node's text is the rendered SVG, so the
                // engine stashes the graph source in data-rio-source. Prefer it; the inner text is
                // only correct before the first render.
                var stashed = node.GetAttributeValue("data-rio-source", null);
                var body = HtmlEntity.DeEntitize(stashed ?? node.InnerText).Trim();
                output.Append("```mermaid\n").Append(body).Append("\n```\n\n");
                return;
            }

            case "div" when IsMath(node):
            {
                var body = HtmlEntity.DeEntitize(node.InnerText).Trim().Trim('$').Trim();
                output.Append("$$\n").Append(body).Append("\n$$\n\n");
                return;
            }

            case "div" or "section" or "article" or "figure" or "tbody" or "thead":
                WriteBlocks(node, output, context);
                return;

            default:
            {
                // Inline-level element sitting at block level (bare <strong>, <a>, <span>, ...).
                var inline = Inline(node).Trim();
                if (inline.Length > 0)
                {
                    output.Append(inline).Append("\n\n");
                }

                return;
            }
        }
    }

    private static void WriteListItem(HtmlNode item, StringBuilder output, ListContext context)
    {
        var marker = context.Ordered
            ? $"{context.Index}. "
            : "- ";

        // Task list: Markdig renders "<li><input type=checkbox disabled checked> text".
        var checkbox = item.SelectSingleNode("./input[@type='checkbox']");
        var task = string.Empty;
        if (checkbox is not null)
        {
            task = checkbox.Attributes.Contains("checked") ? "[x] " : "[ ] ";
            checkbox.Remove();
        }

        var nestedLists = item.ChildNodes
            .Where(n => n.Name is "ul" or "ol")
            .ToArray();
        foreach (var nested in nestedLists)
        {
            nested.Remove();
        }

        var text = Inline(item).Trim();
        output.Append(context.Indent).Append(marker).Append(task).Append(text).Append('\n');

        foreach (var nested in nestedLists)
        {
            WriteBlock(nested, output, context with { Depth = context.Depth + 1 });
        }
    }

    private static void WriteTable(HtmlNode table, StringBuilder output)
    {
        var rows = table.SelectNodes(".//tr");
        if (rows is null || rows.Count == 0)
        {
            return;
        }

        var isFirstRow = true;
        foreach (var row in rows)
        {
            var cells = row.ChildNodes
                .Where(n => n.Name is "td" or "th")
                .Select(cell => Inline(cell).Trim().Replace("|", "\\|"))
                .ToArray();

            if (cells.Length == 0)
            {
                continue;
            }

            output.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");

            if (isFirstRow)
            {
                var alignments = row.ChildNodes
                    .Where(n => n.Name is "td" or "th")
                    .Select(cell => cell.GetAttributeValue("align", string.Empty) switch
                    {
                        "center" => ":---:",
                        "right" => "---:",
                        "left" => ":---",
                        _ => "---"
                    });

                output.Append("| ").Append(string.Join(" | ", alignments)).Append(" |\n");
                isFirstRow = false;
            }
        }

        output.Append('\n');
    }

    /// <summary>Serializes inline content (everything that lives inside a block).</summary>
    private static string Inline(HtmlNode parent)
    {
        var builder = new StringBuilder();

        foreach (var node in parent.ChildNodes)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Text:
                    builder.Append(Normalize(HtmlEntity.DeEntitize(node.InnerText)));
                    continue;

                case HtmlNodeType.Comment:
                    continue;
            }

            switch (node.Name.ToLowerInvariant())
            {
                case "strong" or "b":
                    builder.Append(Wrap(Inline(node), "**"));
                    break;

                case "em" or "i":
                    builder.Append(Wrap(Inline(node), "*"));
                    break;

                case "del" or "s" or "strike":
                    builder.Append(Wrap(Inline(node), "~~"));
                    break;

                case "code":
                {
                    var code = HtmlEntity.DeEntitize(node.InnerText);
                    // Use enough backticks to survive backticks inside the span.
                    var fence = new string('`', LongestBacktickRun(code) + 1);
                    builder.Append(fence).Append(code).Append(fence);
                    break;
                }

                case "a":
                {
                    var href = node.GetAttributeValue("href", string.Empty);
                    var text = Inline(node);
                    builder.Append(string.IsNullOrEmpty(href)
                        ? text
                        : $"[{text}]({href})");
                    break;
                }

                case "img":
                {
                    var src = node.GetAttributeValue("src", string.Empty);
                    var alt = node.GetAttributeValue("alt", string.Empty);
                    builder.Append($"![{alt}]({src})");
                    break;
                }

                case "br":
                    builder.Append("  \n");
                    break;

                case "span" when IsMath(node):
                    builder.Append('$').Append(HtmlEntity.DeEntitize(node.InnerText).Trim('$')).Append('$');
                    break;

                case "input" when node.GetAttributeValue("type", string.Empty) == "checkbox":
                    builder.Append(node.Attributes.Contains("checked") ? "[x] " : "[ ] ");
                    break;

                default:
                    builder.Append(Inline(node));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Wrap(string content, string token)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        // Emphasis markers must hug the text, so leading/trailing spaces move outside.
        var leading = content[..(content.Length - content.TrimStart().Length)];
        var trailing = content[content.TrimEnd().Length..];
        return $"{leading}{token}{content.Trim()}{token}{trailing}";
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static bool IsMermaid(HtmlNode node) =>
        node.GetAttributeValue("class", string.Empty).Contains("mermaid", StringComparison.OrdinalIgnoreCase);

    private static bool IsMath(HtmlNode node) =>
        node.GetAttributeValue("class", string.Empty).Contains("math", StringComparison.OrdinalIgnoreCase);

    private static string LanguageOf(HtmlNode code)
    {
        var css = code.GetAttributeValue("class", string.Empty);
        var token = css.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(c => c.StartsWith("language-", StringComparison.OrdinalIgnoreCase));
        return token is null ? string.Empty : token["language-".Length..];
    }

    /// <summary>Collapses HTML whitespace the way a browser would, keeping non-breaking spaces intact.</summary>
    private static string Normalize(string text)
    {
        var normalized = text.Replace('\u00A0', ' ').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }

        return normalized;
    }
}
