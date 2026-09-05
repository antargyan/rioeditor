using System.Text;
using HtmlAgilityPack;

namespace RioEditor.Core.Sanitization;

/// <summary>
/// HtmlAgilityPack-based whitelist sanitizer. Anything not explicitly allowed is dropped:
/// unknown elements are unwrapped (children survive), dangerous elements are removed wholesale,
/// and every attribute is checked by name and — for URLs — by scheme.
/// </summary>
public sealed class HtmlSanitizerService : IHtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "div", "span", "section", "article",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "strong", "b", "em", "i", "u", "s", "del", "ins", "mark", "sub", "sup", "small",
        "blockquote", "pre", "code", "kbd", "samp", "var",
        "ul", "ol", "li", "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "a", "img", "figure", "figcaption",
        "input" // only type="checkbox", enforced below — Markdig emits these for task lists
    };

    /// <summary>Removed together with their subtree; unwrapping these would leak their payload.</summary>
    private static readonly HashSet<string> StrippedWithChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "object", "embed", "applet", "frame", "frameset",
        "noscript", "style", "link", "meta", "base", "form", "button",
        "textarea", "select", "option", "video", "audio", "source", "track", "svg", "math"
    };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "id", "title", "dir", "lang",
        // Attributes the editor engine itself relies on to address blocks.
        "data-rio-block", "data-rio-id", "data-line", "data-lang"
    };

    private static readonly Dictionary<string, HashSet<string>> TagAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new(StringComparer.OrdinalIgnoreCase) { "href", "target", "rel" },
            ["img"] = new(StringComparer.OrdinalIgnoreCase) { "src", "alt", "width", "height", "loading" },
            ["input"] = new(StringComparer.OrdinalIgnoreCase) { "type", "checked", "disabled" },
            ["td"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan", "align" },
            ["th"] = new(StringComparer.OrdinalIgnoreCase) { "colspan", "rowspan", "align", "scope" },
            ["ol"] = new(StringComparer.OrdinalIgnoreCase) { "start", "type" },
            ["col"] = new(StringComparer.OrdinalIgnoreCase) { "span" }
        };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src"
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel"
    };

    public string Sanitize(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var document = new HtmlDocument
        {
            OptionAutoCloseOnEnd = true,
            OptionFixNestedTags = true,
            OptionWriteEmptyNodes = true
        };
        document.LoadHtml(html);

        CleanNode(document.DocumentNode);

        var writer = new StringWriter(new StringBuilder(html.Length));
        document.DocumentNode.WriteTo(writer);
        return writer.ToString();
    }

    private static void CleanNode(HtmlNode node)
    {
        // Snapshot: the loop mutates the child collection.
        foreach (var child in node.ChildNodes.ToArray())
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Comment:
                    child.Remove();
                    continue;

                case HtmlNodeType.Text:
                    continue;

                case HtmlNodeType.Element:
                    if (StrippedWithChildren.Contains(child.Name))
                    {
                        child.Remove();
                        continue;
                    }

                    if (!AllowedTags.Contains(child.Name))
                    {
                        // Unknown but harmless wrapper: keep the text, drop the element.
                        CleanNode(child);
                        Unwrap(child);
                        continue;
                    }

                    if (child.Name.Equals("input", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(child.GetAttributeValue("type", null), "checkbox",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        child.Remove();
                        continue;
                    }

                    CleanAttributes(child);
                    CleanNode(child);
                    continue;

                default:
                    continue;
            }
        }
    }

    private static void CleanAttributes(HtmlNode element)
    {
        foreach (var attribute in element.Attributes.ToArray())
        {
            var name = attribute.Name;

            // Every event handler (onclick, onload, onerror, ...) goes, plus anything unlisted.
            var allowed = !name.StartsWith("on", StringComparison.OrdinalIgnoreCase) &&
                          (GlobalAttributes.Contains(name) ||
                           (TagAttributes.TryGetValue(element.Name, out var perTag) && perTag.Contains(name)));

            if (!allowed)
            {
                attribute.Remove();
                continue;
            }

            if (UrlAttributes.Contains(name) && !IsSafeUrl(attribute.Value))
            {
                attribute.Remove();
            }
        }

        // Anchors that survive get hardened against reverse-tabnabbing.
        if (element.Name.Equals("a", StringComparison.OrdinalIgnoreCase) &&
            element.Attributes.Contains("target"))
        {
            element.SetAttributeValue("rel", "noopener noreferrer");
        }
    }

    private static bool IsSafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var url = value.Trim();

        // Reject control characters used to smuggle "java\nscript:" past naive checks.
        if (url.Any(char.IsControl))
        {
            return false;
        }

        // Relative URLs, fragments and protocol-relative URLs are fine.
        if (url.StartsWith('#') || url.StartsWith('/') || url.StartsWith('.'))
        {
            return true;
        }

        var colon = url.IndexOf(':');
        if (colon < 0)
        {
            return true; // no scheme at all -> relative
        }

        // A ':' appearing after '/' or '?' belongs to the path, not to a scheme.
        var slash = url.IndexOfAny(['/', '?', '#']);
        if (slash >= 0 && slash < colon)
        {
            return true;
        }

        var scheme = url[..colon];

        // data: is allowed only for inline images, which is how pasted screenshots arrive.
        if (scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) &&
                   !url.Contains("script", StringComparison.OrdinalIgnoreCase);
        }

        return AllowedSchemes.Contains(scheme);
    }

    /// <summary>Replaces an element with its children (HtmlAgilityPack's keepGrandChildren mode).</summary>
    private static void Unwrap(HtmlNode element)
    {
        element.ParentNode?.RemoveChild(element, keepGrandChildren: true);
    }
}
