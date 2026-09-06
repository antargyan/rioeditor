# Store screenshots

Regenerates the App Store screenshots from simulators, so they can be rebuilt whenever the UI
changes rather than being hand-captured once and quietly going stale.

## Why it works this way

The documents are seeded straight into the app's own persistence — a real `.md` file plus a
`settings.json` pointing `lastOpenedFile` at it — rather than typed in. That fixes three things
a manual capture gets wrong:

- the title bar and status line show a **named document**, not `Untitled.md`
- the theme is chosen deterministically instead of by clicking
- `sponsor.dismissed` is forced true, so the sponsorship banner can never appear in a store image

`xcrun simctl status_bar override` sets Apple's marketing status bar (9:41, full signal, full
battery) so every shot looks intentional.

## Sizes

| Slot | Device | Pixels |
| --- | --- | --- |
| iPhone 6.5" | iPhone 11 Pro Max | 1242 × 2688 |
| iPad 13" | iPad Pro 13-inch (M5) | 2064 × 2752 |
| macOS | app window at 1440 × 900 points | 2880 × 1800 |

## Running it

```bash
# create the 6.5" device once
xcrun simctl create "RioShot-6.5" \
  com.apple.CoreSimulator.SimDeviceType.iPhone-11-Pro-Max \
  com.apple.CoreSimulator.SimRuntime.iOS-26-5

# clean build first: an incremental iOS build after changing shared code produces a stale
# AOT image and the app dies at launch with "Failed to load AOT module"
rm -rf src/RioEditor.iOS/bin src/RioEditor.iOS/obj
dotnet build src/RioEditor.iOS -f net10.0-ios -r iossimulator-arm64

xcrun simctl install <udid> src/RioEditor.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/RioEditor.app
xcrun simctl status_bar <udid> override --time "9:41" --batteryState charged --batteryLevel 100 \
  --cellularMode active --cellularBars 4 --wifiMode active --wifiBars 3 --dataNetwork wifi

python3 tools/screenshots/seed.py <udid> ai.rioeditor.editor HERO Light
xcrun simctl launch <udid> ai.rioeditor.editor
xcrun simctl io <udid> screenshot out.png
```

Allow several seconds after launch before capturing: the WebView has to render, highlight code,
and fetch KaTeX and Mermaid from the CDN.

**macOS needs an unlocked session.** Simulator captures read the framebuffer directly and work
regardless, but window capture does not.
