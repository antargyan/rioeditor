# Store credentials setup

Step-by-step for the credentials the publish workflows need. Names here are taken from the
workflows themselves — if you rename a secret, the workflow will not find it.

Three GitHub **environments**, one per store. Create them under
**Settings → Environments** before adding secrets:

| Environment | Used by | Secrets | Status |
| --- | --- | --- | --- |
| `microsoft-store` | `publish-microsoft-store.yml` | 4 | **working** — published a draft submission |
| `google-play` | `publish-google-play.yml` | 5 | **working** — uploaded to the internal track |
| `app-store` | `publish-app-store.yml` | 5 | not set up |
| `apple-developer-id` | `publish-macos.yml` | 5 (3 shared with `app-store`) | not set up |

Environments rather than repository secrets, because these can publish to live listings: a fork's
pull request must not be able to reach them, and required reviewers on an environment double as a
manual approval gate before anything ships.

Repository-level secrets *do* work — a job that declares an environment still inherits them, which
is how the Microsoft Store credentials are set today — but they are readable by every workflow in
the repository, including ones added later. Environment-scoped is the tighter default.

> **Read this before starting the Apple sections.** Both Apple `.p12` exports normally need a Mac.
> Apple issues certificates into Keychain Access, and exporting to `.p12` is a Keychain operation.
> There is a Windows workaround below, but if you have Mac access, do the exports there.

---

## Windows → `microsoft-store`

Already set up and proven. Kept here so the reference is complete, and because the credentials
expire.

**No code-signing secret at all** — the Store re-signs the package itself. That makes Windows the
cheapest of the four to automate: no certificate to buy, renew, or keep out of the repository.

**Prerequisites**

- Partner Center account (US$19 individual / US$99 company; company verification takes days)
- The product reserved, which fixes the identity values below
- The **first** submission completed by hand: screenshots, description, age rating, privacy policy
  URL, and **Device family availability → Windows 10/11 Desktop** ticked

### 1. Associate an Azure AD application

1. Partner Center → **Account settings → User management → Azure AD applications**
2. Associate an existing Entra application, or create one there
3. Give it the **Manager** role — a lesser role authenticates but cannot submit

### 2. Collect the four values

- **Client ID** — the application's ID, on that same page
- **Tenant ID** — the Entra directory ID
- **Client secret** — create one on the application
- **Seller ID** — Partner Center → **Account settings → Account details**

> **This is the step that fails, and it fails opaquely.** In Entra, a client secret shows both a
> **Secret ID** and a **Value**, both GUID-shaped and side by side. You need the **Value**, and it
> is displayed only at creation. Copy the wrong one and `msstore reconfigure` reports
> `Really failed to auth.` with no further detail — the same message you get from an application
> that was never associated with Partner Center, or a tenant from the wrong directory. If the
> Value is no longer visible, create a fresh secret.

### 3. Add the secrets

| Secret | Value |
| --- | --- |
| `PARTNER_CENTER_TENANT_ID` | Entra directory (tenant) ID |
| `PARTNER_CENTER_SELLER_ID` | Partner Center → Account details |
| `PARTNER_CENTER_CLIENT_ID` | the Azure AD application's ID |
| `PARTNER_CENTER_CLIENT_SECRET` | the secret's **Value** |

Nothing else is secret. The package identity and Store ID are in the workflow as plain `env:`
values — the identity is embedded in every published package and the Store ID is in the product's
public URL.

### 4. Note the expiry

Entra caps client secrets at 24 months. When one lapses this pipeline fails exactly as a wrong
secret does, with nothing else changed to explain it. `msstore reconfigure` also accepts a
certificate (`--certificateThumbprint` / `--certificateFilePath`), which is worth preferring for a
pipeline that may go untouched for a year.

---

## Android → `google-play`

**Prerequisites**

- Google Play Developer account (US$25, one-off)
- An app created in the Play Console with package name `ai.rioeditor.editor`
- The **first** AAB uploaded by hand — the API cannot create a listing

### 1. Generate the upload keystore

Once, ever. **Back it up somewhere permanent**: losing this keystore means you can never update
the app under the same listing again.

```bash
keytool -genkeypair -v -keystore rioeditor-upload.jks -alias rioeditor -keyalg RSA -keysize 4096 -validity 10000
```

Note the two passwords it asks for (store password and key password — often set the same).

### 2. Base64-encode it

A keystore is binary, so it travels as base64. On Windows:

```bash
powershell -Command "[Convert]::ToBase64String([IO.File]::ReadAllBytes('rioeditor-upload.jks')) | Set-Clipboard"
```

On macOS or Linux:

```bash
base64 -w0 rioeditor-upload.jks | pbcopy   # or | xclip -selection clipboard
```

### 3. Create a Play service account

1. Play Console → **Setup → API access**
2. Create or link a Google Cloud service account
3. Grant it the **Release Manager** role
4. Create a **JSON key** on that service account and download it

### 4. Add the secrets

| Secret | Value |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | output of step 2 |
| `ANDROID_KEYSTORE_PASSWORD` | store password from step 1 |
| `ANDROID_KEY_ALIAS` | `rioeditor` |
| `ANDROID_KEY_PASSWORD` | key password from step 1 |
| `PLAY_SERVICE_ACCOUNT_JSON` | the entire JSON file contents, verbatim |

### 5. Upload the first bundle by hand

The API can update an app but cannot create one, so the very first AAB has to go up through the
Play Console UI. Run the workflow once to produce a bundle, download the `android-aab` artifact,
and upload that. After that the workflow can take over.

Tag pushes publish; the 16 KB page-alignment blocker is gone. Avalonia 12 brought SkiaSharp
3.119.4 and every shipped 64-bit library is now 16 KB aligned.

### Two things that cost a run here

**`dotnet publish` emits two bundles** — `ai.rioeditor.editor.aab` and
`ai.rioeditor.editor-Signed.aab`. Uploading the unsigned one gets
`All uploaded bundles must be signed. Please sign the bundle using jarsigner.`, which reads like a
signing failure when signing actually worked. The workflow now selects `*-Signed.aab` explicitly
and fails if it is absent.

**`keytool` is not on `PATH` on Windows.** It ships inside the JDK. The one the Android workload
installs lives at:

```
C:\Program Files\Android\openjdk\jdk-21.0.8\bin\keytool.exe
```

Verify a bundle really is signed with the `jarsigner.exe` beside it — `jar verified` plus the
expected `CN=` is the confirmation:

```bash
jarsigner -verify -verbose:summary -certs ai.rioeditor.editor-Signed.aab
```

---

## iOS → `app-store`

**Prerequisites**

- Apple Developer Program membership (US$99/year)
- Bundle identifier `ai.rioeditor.editor` registered in the developer portal
- An app record created in App Store Connect

### 1. Create an App Store Connect API key

1. App Store Connect → **Users and Access → Integrations → App Store Connect API**
2. Generate a key with the **App Manager** role
3. Note the **Issuer ID** (a UUID) and the **Key ID**
4. Download the `.p8` file — **it downloads once only**

### 2. Export the Apple Distribution certificate

Keychain Access → find **Apple Distribution: …** → right-click → **Export** → `.p12`, and set an
export password. Then base64-encode it the same way as the keystore in step 2 above.

### 3. Add the secrets

| Secret | Value |
| --- | --- |
| `ASC_ISSUER_ID` | Issuer ID from step 1 |
| `ASC_KEY_ID` | Key ID from step 1 |
| `ASC_PRIVATE_KEY` | entire contents of `AuthKey_XXXX.p8`, including the BEGIN and END lines |
| `APPLE_DIST_CERT_P12` | base64 of the `.p12` from step 2 |
| `APPLE_DIST_CERT_PASSWORD` | the export password you set |

The workflow uploads to **TestFlight**, never straight to the App Store. Releasing to customers
stays a deliberate act in App Store Connect.

---

## macOS → `apple-developer-id`

Same Apple Developer Program membership. No separate app record is needed for a direct download.

### 1. Export the Developer ID Application certificate

**This is a different certificate from the iOS one.** Developer ID Application signs a direct
download; Apple Distribution signs App Store builds. Do not reuse one for the other.

Keychain Access → **Developer ID Application: …** → Export → `.p12` with a password →
base64-encode.

### 2. Reuse the App Store Connect key

`notarytool` authenticates with the same key as iOS, so the three `ASC_*` values are identical to
the ones in `app-store`. They have to be added again because environment secrets do not carry
across environments.

### 3. Add the secrets

| Secret | Value |
| --- | --- |
| `MACOS_DEVELOPER_ID_CERT_P12` | base64 of the Developer ID Application `.p12` |
| `MACOS_DEVELOPER_ID_CERT_PASSWORD` | its export password |
| `ASC_ISSUER_ID` | same value as in `app-store` |
| `ASC_KEY_ID` | same value as in `app-store` |
| `ASC_PRIVATE_KEY` | same value as in `app-store` |

Notarization is not optional polish: without it Gatekeeper refuses to open the app on another
machine, and that is not a warning a user can reasonably click through.

---

## Exporting a `.p12` without a Mac

Possible, and fiddly. Generate the key and CSR yourself, upload the CSR to the developer portal,
download the issued `.cer`, then combine them:

```bash
openssl genrsa -out private.key 2048
openssl req -new -key private.key -out request.certSigningRequest -subj "/emailAddress=you@example.com, CN=Your Name, C=IN"
# upload request.certSigningRequest at developer.apple.com, download the .cer
openssl x509 -inform DER -in ios_distribution.cer -out cert.pem
openssl pkcs12 -export -inkey private.key -in cert.pem -out cert.p12
```

Keep `private.key`. Without it the certificate cannot be re-exported and has to be revoked and
reissued.

---

## Suggested order

0. **Microsoft Store** — done
1. **Google Play** — done
2. **Apple Developer Program** — one enrolment unlocks both iOS and macOS
3. **App Store Connect API key** — shared by iOS and macOS notarization
4. **The two certificates** — Apple Distribution for iOS, Developer ID Application for macOS

## First run of each workflow

Every one has a safe mode; use it before trusting a tag.

| Workflow | Safe first run | Proven? |
| --- | --- | --- |
| `publish-microsoft-store.yml` | manual with `draft: true` — uploads without committing the submission | yes |
| `publish-google-play.yml` | manual, `track: internal`, `status: draft` | yes |
| `publish-app-store.yml` | manual — TestFlight is not the App Store | no |
| `publish-macos.yml` | manual — produces a **draft** GitHub Release | not end to end; everything up to signing rehearsed locally |

Microsoft Store and Google Play have both run for real and work. iOS and macOS never have — they
were written from each action's documented input contract, so expect the first run of each to need
adjustment.

Two bugs in them were found and fixed *before* any run, by rehearsing the unsigned parts on a Mac:

- `publish-macos.yml` looked for the `.app` under the publish directory. `dotnet publish -o` copies
  the installer `.pkg` there but never the bundle, which stays in the build output — so the search
  returned nothing and the signing step would have run against an empty path. It now searches the
  build output and fails loudly if there is no bundle, and passes `CreatePackage=false` so the
  unused `.pkg` is not built at all.
- `publish-app-store.yml` passed `ArchiveOnBuild=true`, a legacy Xamarin property that does not
  exist in the .NET 10 iOS SDK. Harmless, but misleading: publishing sets `BuildIpa` itself and
  `IpaPackageDir` already defaults to the publish directory.

The rest of the macOS route was exercised locally: the `.app` builds with the right identifier,
version and icon, and a mountable 62 MB DMG is produced with the `/Applications` symlink. Only
signing, notarisation and stapling are unproven, and all three need the certificates. For calibration, both working ones took **two attempts each** with valid credentials:
Windows failed first on the Secret ID / Value confusion, Android on uploading the unsigned
bundle. Neither failure was in the credentials themselves.

## What is not needed

`APPLE_TEAM_ID` appears in [RELEASING.md](RELEASING.md) but is not referenced by any workflow.
Skip it unless something later asks for it.
