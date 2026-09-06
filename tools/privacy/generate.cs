// Generates the hosted privacy page from PRIVACY.md using RioEditor's own export pipeline,
// rather than a hand-rolled Markdown converter. Remote scripts are disabled: a page that says
// the app can avoid network requests should not itself pull anything from a CDN.
using RioEditor.Core.Export;
using RioEditor.Core.Markdown;
using RioEditor.Core.Models;
using RioEditor.Core.Sanitization;

var markdown = File.ReadAllText(args[0]);
var export = new ExportService(new MarkdownService(new HtmlSanitizerService()));
var html = export.BuildStandaloneHtml(markdown, "Privacy Policy — RioEditor",
                                      AppTheme.Light, allowRemoteScripts: false);

// A footer the editor itself would not produce, injected before </body>.
const string footer = """
    <footer style="max-width:46rem;margin:3.5em auto 0;padding-top:1.2em;
                   border-top:1px solid var(--border);color:var(--fg-muted);font-size:.92em">
      RioEditor is free and open source software published by Antargyan under the MIT licence.
      <a href="./">Try the editor in your browser</a> &middot;
      <a href="https://github.com/antargyan/rioeditor">Source code</a>
    </footer>
    """;
html = html.Replace("</body>", footer + "\n</body>")
           .Replace("<head>", "<head>\n<link rel=\"icon\" href=\"favicon.ico\" sizes=\"any\">");

File.WriteAllText(args[1], html);
Console.WriteLine($"wrote {args[1]} ({html.Length} bytes)");
