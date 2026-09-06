# Regenerating the privacy page

`src/RioEditor.Browser/wwwroot/privacy.html` is generated from `PRIVACY.md` by RioEditor's own
export pipeline, so the hosted page cannot drift from the canonical text and inherits the editor's
typography.

```bash
dotnet run --project tools/privacy -- PRIVACY.md src/RioEditor.Browser/wwwroot/privacy.html
```

Two deliberate choices:

- **Remote scripts are disabled** in the export. A page that tells the reader the app can make no
  network requests should not itself pull anything from a CDN, and the generated file has no
  external `<script>` or `<link>` at all.
- **`PRIVACY.md` keeps one logical line per paragraph and per bullet.** The pipeline enables
  `UseSoftlineBreakAsHardlineBreak`, which is right for a WYSIWYG editor but turns source wrapped
  at 100 columns into stray `<br>` elements mid-sentence. Do not re-wrap it.

Edit `PRIVACY.md`, regenerate, and commit both.
