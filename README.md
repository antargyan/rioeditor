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
    ├── Shared.WebKit/              # one WKWebView surface, linked by the macOS and iOS heads
    ├── RioEditor.Desktop/          # Windows / Linux head (WebView2, WebKitGTK)
    ├── RioEditor.Desktop.MacOS/    # macOS head (net10.0-macos, native WKWebView)
    ├── RioEditor.iOS/              # iOS head (net10.0-ios, native WKWebView)
    ├── RioEditor.Android/          # Android head (net10.0-android, system WebView)
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

### iOS

```bash
sudo dotnet workload install ios     # once
dotnet build src/RioEditor.iOS -f net10.0-ios -r iossimulator-arm64
xcrun simctl install booted src/RioEditor.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/RioEditor.app
xcrun simctl launch booted com.rioeditor.app
```

For a physical device you need a provisioning profile; add `-p:RuntimeIdentifier=ios-arm64` and your
signing identity.

### Android

```bash
sudo dotnet workload install android                                    # once
dotnet build src/RioEditor.Android -t:InstallAndroidDependencies \
  -p:AcceptAndroidSDKLicenses=True                                      # once, ~1-2 GB
dotnet build src/RioEditor.Android -t:Run                               # emulator or attached device
```

To install a debug APK by hand, embed the assemblies first — otherwise the runtime expects the
fast-deployment assets that only `-t:Run` pushes:

```bash
dotnet build src/RioEditor.Android -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
adb install -r src/RioEditor.Android/bin/Debug/net10.0-android/com.rioeditor.app-Signed.apk
```

If the JDK or Android SDK live somewhere non-standard, put their paths in
`Directory.Build.local.props` (git-ignored, imported by `Directory.Build.props`):

```xml
<Project>
  <PropertyGroup>
    <JavaSdkDirectory>$(HOME)/Library/Android/jdk/jdk-17.0.20.1+1/Contents/Home</JavaSdkDirectory>
    <AndroidSdkDirectory>$(HOME)/Library/Android/sdk</AndroidSdkDirectory>
  </PropertyGroup>
</Project>
```

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
| iOS | `RioEditor.iOS` | WKWebView (UIKit) | `sudo dotnet workload install ios` + Xcode |
| Android | `RioEditor.Android` | Android System WebView | `sudo dotnet workload install android` + Android SDK |
| WebAssembly | `RioEditor.Browser` | same-origin iframe | None; the browser *is* the WebView |

Every native head is the same shape: a `NativeControlHost` that creates the platform WebView and
implements `IWebViewTransport`. The Apple heads literally share one file (`Shared.WebKit`), because
`WKWebView` is an `NSView` on macOS and a `UIView` on iOS and nothing else differs.

The **inbound** channel is the only part that varies per platform, and `postToHost` in `editor.js`
already covers all of them: `chrome.webview.postMessage` (WebView2),
`webkit.messageHandlers.rio` (WKWebView, macOS *and* iOS), `rioAndroid.postMessage`
(an Android `@JavascriptInterface`), `rioHostChannel` (WebKitGTK) and `parent.postMessage` (WASM).

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

**Export** — HTML and PDF, from the toolbar or `Ctrl+Shift+E` / `Ctrl+P`.

*HTML* is a single self-contained file: the editor's own stylesheet is inlined, along with the same
code highlighter the editor uses, so an export looks like the document you were editing. Print rules
(`@page` margins, `break-inside: avoid` on code, tables and diagrams, and printed URLs after links)
are layered on top. Math and diagrams render on load when remote scripts are enabled.

*PDF* uses three tiers, best first, because the component that already knows how to lay this
document out is the WebView showing it — using its renderer is what makes the PDF match the screen:

| Tier | Platform | Route |
| --- | --- | --- |
| 1 | macOS, iOS | `WKWebView.CreatePdf` returns PDF bytes; the app then shows a save picker |
| 2 | Android | `PrintManager` + the WebView's print adapter opens the system sheet, where "Save as PDF" is always offered |
| 3 | Windows, Linux, WASM | `window.print()` inside the document, and the user picks "Save as PDF" |

A surface opts into tiers 1 and 2 by implementing `IPdfExporter`; anything that does not falls
through to tier 3 automatically.

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

The **iOS** head was deployed to an iPhone 17 Pro simulator and photographed: the document renders in
a real WKWebView, draft persistence works and is correctly scoped to the app's own container, and
the status bar reports a live word count. Verifying it also turned up a genuine bug — word count
stayed at 0 for a freshly opened document, because it was only ever updated on edit. The engine now
reports document stats after a full mount, separately from `docChanged`, so opening a file no longer
looks empty and does not mark the buffer dirty.

The **Android** head was deployed to an Android 36 (API 36, arm64) emulator and photographed: the
document renders in the system WebView, the word count reads 144, and tapping the theme toggle flips
both the Avalonia chrome and the WebView together. That word count matters — it can only have
arrived through the `@JavascriptInterface` channel, so the Android transport is proven in *both*
directions, not just outbound.

Two Android-specific traps, both hit and fixed here:

1. The activity theme **must** descend from `Theme.AppCompat`. Avalonia's `AvaloniaActivity` extends
   `AppCompatActivity`, so any other parent (e.g. `android:Theme.Material.Light.NoActionBar`) dies at
   startup with *"You need to use a Theme.AppCompat theme"*.
2. Installing the debug APK by hand with `adb install` crashes with *"No assemblies found …
   Assuming this is part of Fast Deployment"*. Either deploy with `dotnet build -t:Run`, which pushes
   the assemblies separately, or build with `-p:EmbedAssembliesIntoApk=true` as the commands in
   section 4 do.

The **Windows/Linux** desktop head builds and starts; its WebView2 and WebKitGTK backends are the
well-trodden paths for those platforms and were not exercised on this machine.

### Responsive chrome

The top chrome has two layouts, chosen from the control's own width (breakpoint 700px in
`MainView.axaml.cs`) rather than from a device check — so a narrow desktop window compacts too, and
a tablet or landscape phone keeps the full toolbar.

| | Wide | Compact |
| --- | --- | --- |
| Row 1 | file commands · formatting toolbar · theme | ☰ · document name · theme |
| Row 2 | — | full-width scrolling formatting bar |
| Targets | 32×30 | 44×44 (platform minimum for a comfortable tap) |
| File commands | inline buttons | inline row toggled by ☰ |

**The compact file menu is an inline row, not a `Flyout`, and that is deliberate.** The editing
surface is a *native* WebView layered above Avalonia's canvas, so any Avalonia popup overlapping it
is drawn behind it. The first attempt used a `MenuFlyout` and it rendered clipped — only the part
above the WebView was visible. Anything that must stay visible on a native-WebView platform has to
live inside the chrome. The same applies to any future dropdown, autocomplete or context menu.

### Known limitations

- Mobile contenteditable has its own selection and virtual-keyboard behaviour that has not been
  exercised beyond rendering, layout and load.
- The formatting bar does not follow the on-screen keyboard; a format bar docked above the keyboard
  would be the next improvement.
- No export or print.

### Rendered blocks must keep their source

Both Mermaid and KaTeX *replace* the content they render, which caused a family of bugs found by
printing the document and looking at the preview:

- Re-running Mermaid after a theme flip fed it the rendered SVG instead of the graph, producing
  "Syntax error in text";
- KaTeX's auto-render re-processed its own output on every decoration pass, multiplying the
  expression;
- worst of all, the HTML → Markdown pass read a rendered diagram's text, so **saving after a diagram
  rendered wrote SVG into the Markdown file**.

The engine now stashes the original graph in `data-rio-source` before rendering and restores it
before any re-render, `HtmlToMarkdownService` prefers that attribute over the node's text, and
KaTeX is configured with `ignoredClasses: ['katex', 'katex-display']`. Any future renderer that
rewrites its own block needs the same treatment.

### Gotcha: stale iOS builds

An incremental iOS build after changing shared code can produce
*"Failed to load AOT module … while running in aot-only mode"* and an immediate SIGABRT. Delete the
project's `bin`/`obj` and rebuild.

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
