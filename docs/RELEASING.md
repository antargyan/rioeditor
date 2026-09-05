# Releasing RioEditor

The delivery plan for the six targets, what each one needs, and what is still missing.
Nothing here is wired up yet beyond CI — the release workflows are deliberately not committed
until the accounts and secrets below exist, because a half-configured signing pipeline fails in
confusing ways months later.

## Status

| Target | Distribution | Signing needed | Ready? |
| --- | --- | --- | --- |
| WebAssembly | GitHub Pages | none | **Live** — `pages.yml` deploys every push to `main` |
| Windows | Microsoft Store (MSIX) | none — the Store re-signs | **Package builds and validates**; awaiting listing content |
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

## Windows → Microsoft Store

Built by `packaging/windows/build-msix.ps1`, which publishes both architectures, packs them and
bundles the result. MSIX rather than an EXE/MSI listing for one decisive reason: **the Store
re-signs MSIX packages with its own certificate**, so there is no Authenticode certificate to buy
or renew, and no SmartScreen warning. Updates and clean uninstall come free with it.

```powershell
powershell.exe -ExecutionPolicy Bypass -File packaging\windows\build-msix.ps1 `
    -IdentityName 'CDUCK.RioEditorMarkDownEditor' `
    -Publisher 'CN=48B5EDEA-A9A5-4B78-A27C-36E0547C8A22' `
    -PublisherDisplayName 'ANTARGYAN CLOUDWORKS LLP'
```

Needs the Windows SDK for `makeappx.exe` and `signtool.exe`; the script finds the newest installed
one itself. Output lands in `publish/store/`:

| File | Purpose |
| --- | --- |
| `RioEditor_<version>.msixbundle` | upload this — unsigned is correct, the Store signs it |
| `RioEditor_<version>_test-signed.msixbundle` | self-signed, for installing on your own machine |
| `RioEditorTest.cer` | trust once to install the test-signed bundle |

Each architecture is published **self-contained** (~50 MB each, ~98 MB bundled): an MSIX cannot run
a prerequisite installer, so the .NET runtime travels inside the package.

### The four values that must match Partner Center

Three come from **Product → Product identity** and one from **Manage app names**. Every one of them
is validated on upload, and three of the four produce an outright rejection:

| Manifest field | Parameter | Current value |
| --- | --- | --- |
| `Package/Identity/Name` | `-IdentityName` | `CDUCK.RioEditorMarkDownEditor` |
| `Package/Identity/Publisher` | `-Publisher` | `CN=48B5EDEA-A9A5-4B78-A27C-36E0547C8A22` |
| `Package/Properties/PublisherDisplayName` | `-PublisherDisplayName` | `ANTARGYAN CLOUDWORKS LLP` |
| `Package/Properties/DisplayName` | `-DisplayName` | `RioEditor : MarkDown Editor` |

**`DisplayName` must be a name you have reserved**, not a friendly label. Setting it to `RioEditor`
fails the upload with *"uses a display name that you have not reserved"* even though the identity
name contains it. Reserve additional names under **Product management → Manage app names** if you
want a shorter caption under the Start-menu tile.

There is a good self-check for the other two: Windows derives the Package Family Name from the
identity name and publisher, so registering the package locally and comparing
`(Get-AppxPackage).PackageFamilyName` against the PFN in Partner Center
(`CDUCK.RioEditorMarkDownEditor_ehpcqv5z78m2c`) proves both are byte-exact before you upload.

### Versioning

`-Version` defaults to `RioVersion` from `Directory.Build.props` with `.0` appended, so the package
does not keep a second copy of the product version.

`RioBuild` deliberately does *not* become the fourth field. The Store reserves the revision and
rejects anything non-zero there, so a second upload of the same `RioVersion` needs `RioVersion`
bumped (or `-Version` passed explicitly) — the Store will not accept a version it has already seen.

### Tile artwork

`packaging/windows/generate-icons.ps1` composites `src/RioEditor.App/Assets/icon.png` — the same
glyph Android, iOS and macOS use — into all 25 tile sizes, so Windows cannot drift from the other
platforms. Change the artwork there and re-run the script.

Two decisions worth knowing before editing it:

- **The tiles are transparent PNGs, not white ones.** Windows fills a tile with the manifest's
  `VisualElements/@BackgroundColor` and draws its own plate behind the plated `targetsize` assets;
  baking a background into the PNG fights both. `BackgroundColor` is `#FFFFFF`, matching
  `rio_icon_background` in the Android adaptive icon and the ground of the macOS iconset.
- **The glyph fills less of the canvas as tiles grow** (88% at 16px down to 62% at 310px) — the
  same reasoning as the Android adaptive icon's 18% foreground inset.

The `.exe` icon is *not* generated here; `src/RioEditor.Desktop/RioEditor.ico` is checked in and
referenced by the csproj.

### File associations

The manifest claims `.md` and `.markdown`, which puts RioEditor in the *Open with* list. Windows
starts a packaged full-trust app with the chosen file's path on its command line, so this needs no
association-specific code — `Program.Main` hands its arguments to `StartupDocument`.

Only those two extensions, deliberately. Claiming `.txt`, or the long tail of `.mdown`/`.mkd`,
draws Store review questions and annoys users whose defaults get hijacked.

### Before submitting

Partner Center still needs screenshots, a description, an age rating and a privacy policy URL, and
**Device family availability → Windows 10/11 Desktop must be ticked** or the product ships to
nobody. The listing icon is generated as `packaging/windows/Images/StoreListing-300x300.png`.

## Linux

No account needed. Tag → publish self-contained binaries → attach to a GitHub Release.

## WebAssembly → GitHub Pages

Wired up in `.github/workflows/pages.yml`; every push to `main` republishes
<https://antargyan.github.io/rioeditor/>. No secrets involved.

**One-time setup:** repository *Settings → Pages → Build and deployment → Source: **GitHub Actions***.
Without that the workflow runs and then fails at the deploy step.

Three things the workflow does that a naive copy of `wwwroot` would miss, each verified locally by
serving the output from a sub-path:

1. **Rewrites `<base href>`** from `/` to `/rioeditor/`. A project site is not at the domain root,
   so the published default sends every asset request to the wrong place and the app never boots.
2. **Deletes the `.gz`/`.br` duplicates.** Publish emits three copies of everything; Pages does its
   own compression and the loader fetches the plain files. 37 MB becomes 22 MB.
3. **Adds `.nojekyll`**, so the `_framework` directory is never stripped as an underscore path.

AOT is left off. `-p:RunAOTCompilation=true` shrinks and speeds up the payload but adds several
minutes per build; worth enabling once the demo gets real traffic.

## Suggested order

1. **WASM on Pages** — free, immediate, and gives the README a live demo link.
2. **Microsoft Store** — no certificate to buy, because the Store signs the package, and the
   identity is already reserved; the remaining work is listing content, not engineering.
3. **GitHub Releases for macOS and Linux** — unsigned first, so people can try it.
4. **Apple Developer Program** — unlocks notarised macOS *and* iOS together.
5. **Google Play** — cheapest store, and the Android head is the best tested of the mobile two.

## Version numbering

`Directory.Build.props` holds the single source of truth:

- **`RioVersion`** — the semantic, user-visible version (`1.0.0`)
- **`RioBuild`** — a monotonic build number, which the stores require to increase on *every* upload
  regardless of whether the display version changed

Both are ordinary MSBuild properties, so CI overrides them from a tag without editing any file —
command-line properties are global in MSBuild and win over the defaults:

```bash
dotnet build -p:RioVersion=1.4.2 -p:RioBuild=57
```

Verified end to end on all three store platforms:

| Platform | Field | Value with the override above |
| --- | --- | --- |
| macOS / iOS | `CFBundleShortVersionString` | `1.4.2` |
| macOS / iOS | `CFBundleVersion` | `57` |
| Android | `android:versionName` | `1.4.2` |
| Android | `android:versionCode` | `57` |

**Do not reintroduce `CFBundleVersion` or `CFBundleShortVersionString` into the Info.plist files.**
The Apple SDKs only inject those keys when the plist does not already define them, so a hard-coded
value silently wins and every build ships the same version — which App Store Connect rejects on the
second upload, confusingly, as a duplicate build number.

In a release workflow, derive both from the tag:

```yaml
- run: |
    VERSION=${GITHUB_REF_NAME#v}          # v1.4.2 -> 1.4.2
    echo "RIO_VERSION=$VERSION"      >> $GITHUB_ENV
    echo "RIO_BUILD=$GITHUB_RUN_NUMBER" >> $GITHUB_ENV
- run: dotnet publish ... -p:RioVersion=$RIO_VERSION -p:RioBuild=$RIO_BUILD
```
