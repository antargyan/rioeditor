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
| iPhone 6.9" | iPhone 17 Pro Max | 1320 × 2868 |
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

## macOS

```bash
python3 tools/screenshots/seed-mac.py IPAD_A Light
open src/RioEditor.Desktop.MacOS/bin/Release/net10.0-macos/osx-arm64/RioEditor.app
osascript -e 'tell application "System Events" to tell process "RioEditor"
  set frontmost to true
  set position of window 1 to {40, 60}
  set size of window 1 to {1280, 800}
end tell'
screencapture -x -o -R40,60,1280,800 out.png     # 2560 x 1600 on a Retina display
```

**Use 1280 x 800, not 1440 x 900.** Both are accepted App Store sizes, but AppKit clamps a window
to the visible frame — screen minus menu bar minus Dock — and on a 1512 x 982 point display that
caps the height at about 864. Asking for 900 silently yields a shorter window, and the capture
region then includes a strip of desktop below it. 1280 x 800 fits with room to spare.

**macOS needs an unlocked session.** Simulator captures read the framebuffer directly and work
regardless, but window capture does not: on a locked Mac the window is not in the accessibility
tree at all, and `System Events` reports "Can't get window 1".
