namespace RioEditor.Core.Sanitization;

/// <summary>
/// Whitelist sanitizer applied to every piece of HTML before it reaches the editing surface.
/// Both directions matter: rendered Markdown may contain raw HTML, and content pasted into the
/// contenteditable arrives from the clipboard entirely unvetted.
/// </summary>
public interface IHtmlSanitizer
{
    string Sanitize(string html);
}
