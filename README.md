# RioEditor

A Typora-style WYSIWYG Markdown editor built with **AvaloniaUI 11.3** on **.NET 10**, running on
Windows, macOS, Linux and WebAssembly.

There is no split view and no preview pane. The rendered HTML *is* the document you type into:
Markdown syntax is applied and then disappears as you write, and the caret never jumps.

---

## 1. Project layout

```
RioEditor/
├── RioEditor.slnx                  # .NET 10 solution
├── Directory.Build.props           # single source of truth for package versions
├── global.json                     # SDK pin (10.0.400, rollForward latestFeature)
└── src/
    ├── RioEditor.Core/             # platform-neutral: no Avalonia, no UI
    │   ├── Models/                 # DocumentModel, AppSettings
    │   ├── Markdown/               # Markdig pipeline, Mermaid extension, HTML→Markdown
    │   ├── Sanitization/           # HtmlAgilityPack whitelist sanitizer
    │   ├── Storage/                # IKeyValueStore + file implementation
    │   ├── Settings/               # SettingsService
    │   ├── Editor/                 # WebViewBridge + protocol + document factory
    │   └── Assets/                 # editor.html · editor.css · editor.js (embedded)
    ├── RioEditor.App/              # shared Avalonia UI
    │   ├── ViewModels/             # MainViewModel, ToolbarViewModel (ReactiveUI)
    │   ├── Views/                  # MainWindow · MainView · ToolbarView
    │   ├── Services/               # FileService, ThemeService, surface abstraction
    │   └── Composition/            # Microsoft.Extensions.DependencyInjection wiring
    ├── RioEditor.Desktop/          # Windows / Linux head (WebView2, WebKitGTK)
    ├── RioEditor.Desktop.MacOS/    # macOS head (net10.0-macos, native WKWebView)
    └── RioEditor.Browser/          # WebAssembly head + iframe surface + JS interop
```

The layering rule: `Core` knows nothing about Avalonia, `App` knows nothing about which WebView it
is talking to, and each head supplies exactly two things — an `IEditorSurface` and an
`IKeyValueStore`.

---

## 2. How the live pipeline works

A naive WYSIWYG loop ("on every keystroke: read HTML → Markdown → HTML → write back") destroys the
caret and feels laggy. RioEditor splits the work by latency budget:

| When | Where | What happens |
| --- | --- | --- |
| Every keystroke | JS engine | Inline rules (`**b**`, `` `c` ``, `[t](u)`, `~~s~~`) rewrite the DOM the instant the closing token is typed. No round trip. |
| Space / Enter | JS engine | Block rules (`# `, `> `, `- `, `1. `, `- [ ] `, ```` ``` ````, `---`) restructure the current block locally. |
| Caret leaves a block | JS → C# → JS | That **one block** is round-tripped: HTML → Markdown (HtmlAgilityPack) → HTML (Markdig) → sanitized → swapped back in, with the caret restored by character offset. |
| Open / `setMarkdown` | C# | Full-document render through the Markdig pipeline. |
| Paste | C# | Clipboard HTML is sanitized and normalised before it is allowed into the document. |
| Every 250 ms of idle | JS → C# | `docChanged` fires; the host updates the Markdown, word count and dirty flag. |

Caret preservation uses two strategies: a node-path + offset pair for local DOM surgery, and a
character offset within the block for the re-render swap (which survives a complete rebuild of that
block's DOM).

**Markdown lives in exactly one place.** The JS engine never implements a Markdown parser or
serializer — `RioEditor.getMarkdown()` and `setMarkdown()` are promises that delegate to the host.

### Message protocol

```
host   → engine :  window.rio.receive('{"type":"setHtml"|"blockRendered"|"command"|"setTheme"|…}')
engine → host   :  postToHost('{"type":"ready"|"docChanged"|"renderBlock"|"hostRequest"|"selection"|…}')
```

`postToHost` picks the first available channel: `chrome.webview.postMessage` (WebView2),
`webkit.messageHandlers.rio` (WKWebView), `window.rioHostChannel` (WebKitGTK shim) or
`parent.postMessage` (the WASM iframe).

---

## 3. Markdown features

Enabled in `MarkdownService.BuildPipeline()`:

- Advanced extensions (emphasis extras, definition lists, abbreviations, attributes, containers)
- Pipe tables and grid tables
- Task lists (rendered as live, clickable checkboxes)
- Footnotes, auto-links, emoji/smiley shortcodes
- YAML front matter
- Mathematics → KaTeX (`$inline$`, `$$display$$`)
- **Custom Mermaid extension** — ```` ```mermaid ```` becomes `<div class="mermaid">`
- Syntax highlighting for fenced code (dependency-free, in the engine)

Every rendered fragment passes through `HtmlSanitizerService` before it reaches the surface:
`<script>`, `<iframe>`, `<object>`, `<style>`, `<form>` and friends are removed with their subtree;
unknown elements are unwrapped; all `on*` handlers are stripped; `href`/`src` are restricted to
`http`, `https`, `mailto`, `tel`, relative URLs, and `data:image/*`.

---

## 4. Building and running

```bash
dotnet restore RioEditor.slnx
dotnet build RioEditor.slnx
```

### Desktop (Windows, Linux)

```bash
dotnet run --project src/RioEditor.Desktop
```

### Desktop (macOS)

macOS has its own head, because it needs the modern AppKit/WebKit bindings (see section 5).
It requires the macOS workload once:

```bash
sudo dotnet workload install macos
```

```bash
dotnet run --project src/RioEditor.Desktop.MacOS
```

Publish:

```bash
dotnet publish src/RioEditor.Desktop -c Release -r win-x64   --self-contained -o publish/win
dotnet publish src/RioEditor.Desktop -c Release -r linux-x64 --self-contained -o publish/linux
dotnet publish src/RioEditor.Desktop.MacOS -c Release -r osx-arm64 -o publish/mac   # produces RioEditor.app
```

Note that `dotnet build RioEditor.slnx` requires the macOS workload, because the solution includes
that head. Without it you get `NETSDK1147`; build the other projects individually, or drop the
`RioEditor.Desktop.MacOS` line from `RioEditor.slnx`.

### WebAssembly

```bash
dotnet workload install wasm-tools          # once
dotnet run --project src/RioEditor.Browser  # http://localhost:5000
```

Publish a static site:

```bash
dotnet publish src/RioEditor.Browser -c Release -o publish/wasm
# serve publish/wasm/wwwroot with any static file server:
npx serve publish/wasm/wwwroot
```

For a smaller/faster bundle add AOT (needs the `wasm-tools` workload and a few minutes):

```bash
dotnet publish src/RioEditor.Browser -c Release -p:RunAOTCompilation=true -o publish/wasm
```

Serve the output with `Content-Type: application/wasm` for `.wasm` and, if you enable
multithreading later, the COOP/COEP headers. Plain single-threaded builds need no special headers.

---

## 5. WebView prerequisites per platform

| Platform | Head | Backend | Requirement |
| --- | --- | --- | --- |
| Windows | `RioEditor.Desktop` | WebView2 (via WebView.Avalonia) | [Evergreen WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (present on Windows 11, shipped by Edge on Windows 10) |
| Linux | `RioEditor.Desktop` | WebKitGTK (via WebView.Avalonia) | `sudo apt install libwebkit2gtk-4.1-0` (older distros: `libwebkit2gtk-4.0-37`) |
| macOS | `RioEditor.Desktop.MacOS` | WKWebView (native bindings) | `sudo dotnet workload install macos` |
| WebAssembly | `RioEditor.Browser` | same-origin iframe | None; the browser *is* the WebView |

**Why macOS gets its own head.** `WebView.Avalonia` (v11.0.0.1, the newest published) resolves its
macOS backend through the legacy `Xamarin.Mac` bindings, whose type initializer throws on the .NET
10 runtime — confirmed on this machine, where the app came up and showed its diagnostic panel
reading *"The type initializer for 'Builder.NativeImplementationBuilder' threw an exception."*

`RioEditor.Desktop.MacOS` targets `net10.0-macos` instead, which brings the modern AppKit/WebKit
bindings, and hosts a real `WKWebView` inside an Avalonia `NativeControlHost`. Messages come back
through a `WKScriptMessageHandler` registered as `"rio"` — which is exactly the
`window.webkit.messageHandlers.rio` branch already present in `postToHost`, so **the JavaScript
engine needed no changes at all**. Avalonia and the ObjC runtime bridge share `NSApplication`
without conflict.

The failure path stayed useful throughout: `WebViewEditorSurface` catches backend creation failures
and `MainView` shows a diagnostic panel rather than crashing or showing an empty window. If you
ever need a fourth backend, implementing `IEditorSurface` + `IWebViewTransport` (~150 lines) and
registering it in `Program.BuildServices()` is the whole job.

---

## 6. Features

**Toolbar** — bold, italic, strikethrough, inline code, headings 1–6 (and clear), link, code block,
quote, bullet/numbered/task lists, 3×3 table, horizontal rule, theme toggle.

**Shortcuts** — `Ctrl/Cmd+B` bold, `+I` italic, `+E` inline code, `+K` link, `+1…6` heading,
`+0` paragraph, `Ctrl+N/O/S/Shift+S` file commands, `Ctrl+Alt+T` theme, `Tab`/`Shift+Tab` list
indent.

**File I/O** — open/save through Avalonia's `IStorageProvider` (native dialogs on desktop, File
System Access API or a download in the browser), autosave every 5 seconds, dirty tracking in the
title bar, last file restored on startup. When there is no writable path (unsaved buffer, or WASM),
autosave keeps a draft in the settings store instead of losing work.

**Settings** — theme, last opened file, window size/maximised state, autosave interval and the WASM
compatibility switches, persisted to `settings.json` in the user's app-data folder on desktop and to
`localStorage` in the browser.

---

## 7. WebAssembly notes and limitations

- **No native WebView.** The editor document runs in a same-origin `<iframe srcdoc>` layered over
  the Avalonia canvas; `BrowserEditorSurface` keeps it aligned with the placeholder control on every
  layout pass.
- **No file system.** `FileService.SupportsDirectFileAccess` is false; saving goes through the
  browser's save picker (or a download), and "last opened file" becomes "last draft".
- **Remote scripts.** Mermaid and KaTeX load from jsDelivr. Set
  `AppSettings.Wasm.AllowRemoteScripts = false` for an offline or CSP-locked deployment — those
  blocks then render as plain fenced code, and nothing else changes.
- **Trimming.** `PublishTrimmed` is on with `TrimMode=partial`, which keeps Avalonia's XAML loader
  and ReactiveUI's reflection working. Set `PublishTrimmed=false` if you add reflection-heavy code.
- **Storage quota.** `localStorage` is capped (~5 MB) and throws in private mode; every access in
  `rio-interop.js` is wrapped in try/catch and degrades to "no persistence".

---

## 8. What was verified on this machine

Built and run on macOS with .NET 10.0.400. `dotnet build RioEditor.slnx` succeeds with zero warnings
for all four projects.

The **WebAssembly** head was driven end to end in a real browser:

- Avalonia shell, toolbar and status bar render; the editor iframe is created and tracks the layout
- The welcome document renders through Markdig → sanitizer → engine
- Typing `**bold**`, `*italic*`, `` `code` ``, `~~strike~~`, `[a link](url)` transforms in place and
  the syntax markers disappear — verified by asserting on the resulting DOM
- Typing `## ` / `### ` converts the block to a heading
- A toolbar command (bullet list) sent host → engine restructured the block
- `RioEditor.getMarkdown()` round-tripped `### Heading with **bold** and [a link](https://example.com)`
  back out byte-for-byte through HtmlAgilityPack
- Autosave fired on its 5 s timer and kept a draft; the draft and the dark theme both survived a
  full page reload

Three real bugs were found and fixed during that pass, which is worth knowing if you extend the code:

1. `JsonSerializer` with anonymous types throws `JsonSerializerIsReflectionDisabled` in a trimmed
   WASM build — all serialization now goes through source-generated contexts (`EditorJsonContext`,
   `SettingsJsonContext`). Keep it that way when you add message fields.
2. A source-generated context bakes its options in; cloning `JsonSerializerOptions` and assigning
   `TypeInfoResolver` loses the metadata at runtime (`NoMetadataForType`). Hence two contexts.
3. `JSHost.ImportAsync` resolves module URLs relative to `_framework/`, not the site root.

The **macOS** head was run and photographed: the welcome document renders in a real WKWebView with
headings, emphasis, inline code, interactive task-list checkboxes, the syntax-highlighted code block
and the table all correct; live typing updates the word count; autosave keeps a draft; the title bar
shows the dirty marker. Avalonia's chrome and the WebView share the window without conflict.

The **Windows/Linux** desktop head builds and starts; its WebView2 and WebKitGTK backends are the
well-trodden paths for those platforms and were not exercised on this machine.

## 9. Package versions

Pinned centrally in `Directory.Build.props`:

| Package | Version |
| --- | --- |
| Avalonia (+ Themes.Fluent, Fonts.Inter, ReactiveUI, Desktop, Browser) | 11.3.9 |
| WebView.Avalonia (+ .Desktop) | 11.0.0.1 |
| Markdig | 1.3.2 |
| HtmlAgilityPack | 1.13.0 |
| Microsoft.Extensions.DependencyInjection | 10.0.0 |

`Avalonia.ReactiveUI` is the version ceiling: it is published up to 11.3.9, so the whole Avalonia
stack is pinned there for consistency.
