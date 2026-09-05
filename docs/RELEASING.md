# Releasing RioEditor

The delivery plan for the six targets, what each one needs, and what is still missing.
Nothing here is wired up yet beyond CI — the release workflows are deliberately not committed
until the accounts and secrets below exist, because a half-configured signing pipeline fails in
confusing ways months later.

## Status

| Target | Distribution | Signing needed | Ready? |
| --- | --- | --- | --- |
| WebAssembly | GitHub Pages (or any static host) | none | **Yes** — CI already publishes the site as an artifact |
| Windows | GitHub Release (zip), optionally MSIX | Authenticode (optional) | Buildable; unsigned binaries warn on SmartScreen |
| Linux | GitHub Release (tar.gz / AppImage) | none | Buildable |
| macOS | Notarized DMG, and/or Mac App Store | Apple Developer ID or Mac App Distribution | Needs Apple account |
| iOS | App Store / TestFlight | Apple Distribution | Needs Apple account |
| Android | Google Play, and an APK on GitHub Releases | Upload keystore + Play service account | Needs Play account |

Bundle identifier across Apple and Android: `ai.rioeditor.editor`.

## What CI does today

`.github/workflows/ci.yml` builds every head on each push and pull request. It cannot build the
whole solution in one job — `RioEditor.slnx` contains heads that require the `macos`, `ios` and
`android` workloads — so the work is split by runner:

- **ubuntu**: Core, App, Desktop (Windows/Linux head), Browser; publishes the WASM site as an artifact
- **macos-14**: the macOS head and the iOS head (simulator build, which needs no signing)
- **ubuntu**: the Android head (the hosted image already has the SDK and a JDK)

Note that macOS runner minutes bill at 10x on private repositories. Making the repository public
makes Actions free, which is the cheapest way to keep the Apple jobs running.

## Android → Google Play

**Pipeline:** tag `v*` → build AAB → sign with the upload keystore → upload to the Play
Console's internal track → promote manually.

```
dotnet publish src/RioEditor.Android -c Release -p:AndroidPackageFormat=aab
```

**Needed from you**

1. A **Google Play Developer account** (one-off US$25).
2. An app created in the Play Console with package name `ai.rioeditor.editor`. The *first* upload
   must be done by hand; the API cannot create the listing.
3. An **upload keystore**, generated once and backed up somewhere safe — losing it means you can
   never update the app under the same listing:
   ```
   keytool -genkeypair -v -keystore rioeditor-upload.jks -alias rioeditor \
           -keyalg RSA -keysize 4096 -validity 10000
   ```
4. A **Play service account** with the "Release manager" role, and its JSON key.

**Repository secrets**

| Secret | Contents |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | `base64 -i rioeditor-upload.jks` |
| `ANDROID_KEYSTORE_PASSWORD` | keystore password |
| `ANDROID_KEY_ALIAS` | `rioeditor` |
| `ANDROID_KEY_PASSWORD` | key password |
| `PLAY_SERVICE_ACCOUNT_JSON` | the service-account JSON, verbatim |

## iOS → App Store / TestFlight

**Pipeline:** tag `v*` → archive on a macOS runner → sign → upload to App Store Connect →
release from TestFlight.

**Needed from you**

1. **Apple Developer Program** membership (US$99/year). Note the **Team ID**.
2. The bundle identifier `ai.rioeditor.editor` registered in the developer portal, and an app
   record created in App Store Connect.
3. An **App Store Connect API key** (Users and Access → Integrations → App Store Connect API),
   role "App Manager". This gives the Issuer ID, the Key ID and a `.p8` file that downloads once.
4. A **distribution certificate** and provisioning profile. Simplest is to let `fastlane match`
   manage them in a private repository; otherwise export the `.p12` by hand.

**Repository secrets**

| Secret | Contents |
| --- | --- |
| `APPLE_TEAM_ID` | 10-character team identifier |
| `ASC_ISSUER_ID` | App Store Connect API issuer UUID |
| `ASC_KEY_ID` | API key identifier |
| `ASC_PRIVATE_KEY` | contents of the `AuthKey_*.p8` |
| `APPLE_DIST_CERT_P12` | base64 of the distribution certificate |
| `APPLE_DIST_CERT_PASSWORD` | its password |

## macOS → notarized DMG (and optionally the Mac App Store)

Two independent routes; the direct download is the one to do first, since it has no review queue.

**Direct download.** Build the `.app`, sign with a **Developer ID Application** certificate,
staple after notarisation:

```
dotnet publish src/RioEditor.Desktop.MacOS -c Release -r osx-arm64
codesign --deep --force --options runtime --sign "Developer ID Application: ..." RioEditor.app
xcrun notarytool submit RioEditor.dmg --key AuthKey.p8 --key-id ... --issuer ... --wait
xcrun stapler staple RioEditor.dmg
```

Without this, macOS Gatekeeper refuses to open the app at all on another machine — this is not a
warning that can be clicked through easily, so it is the difference between a usable download and
an unusable one.

**Needed from you:** the same Apple Developer Program membership, plus a **Developer ID
Application** certificate exported as `.p12`. The Mac App Store route additionally needs a **Mac
App Distribution** certificate and a separate app record.

Ship both architectures: `-r osx-arm64` and `-r osx-x64`, joined with `lipo`, or two DMGs.

## Windows and Linux

No accounts needed. Tag → publish self-contained binaries → attach to a GitHub Release.

An Authenticode certificate (~US$200–400/year from a CA) removes the SmartScreen warning on
Windows. Worth deferring until there are users to warn.

## WebAssembly → GitHub Pages

The cheapest thing to ship, and the best demo for the sponsorship page: `dotnet publish` output is
a static site. Enable Pages on the repository, point it at the `wasm-site` artifact, and every push
to `main` republishes. This works today with no secrets at all.

## Suggested order

1. **WASM on Pages** — free, immediate, and gives the README a live demo link.
2. **GitHub Releases for Windows, macOS, Linux** — unsigned first, so people can try it.
3. **Apple Developer Program** — unlocks notarised macOS *and* iOS together.
4. **Google Play** — cheapest store, and the Android head is the best tested of the mobile two.

## Version numbering

One source of truth is missing: `ApplicationDisplayVersion` and `ApplicationVersion` are currently
hard-coded per head. Before the first release, lift them into `Directory.Build.props` so a tag can
drive every platform's version at once.
