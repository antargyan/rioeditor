# Store credentials setup

Step-by-step for the credentials the publish workflows need. Names here are taken from the
workflows themselves — if you rename a secret, the workflow will not find it.

Three GitHub **environments**, one per store. Create them under
**Settings → Environments** before adding secrets:

| Environment | Used by | Secrets |
| --- | --- | --- |
| `google-play` | `publish-google-play.yml` | 5 |
| `app-store` | `publish-app-store.yml` | 5 |
| `apple-developer-id` | `publish-macos.yml` | 5 (3 shared with `app-store`) |

Environments rather than repository secrets, because these can publish to live listings: a fork's
pull request must not be able to reach them, and required reviewers on an environment double as a
manual approval gate before anything ships.

> **Read this before starting the Apple sections.** Both Apple `.p12` exports normally need a Mac.
> Apple issues certificates into Keychain Access, and exporting to `.p12` is a Keychain operation.
> There is a Windows workaround below, but if you have Mac access, do the exports there.

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

> **The Play workflow is manual-only right now.** Google Play rejects 64-bit native libraries
> that are not 16 KB page aligned on Android 16, and the `libSkiaSharp.so` we ship is 4 KB
> aligned. Fixed by the Avalonia 12 migration — see [RELEASING.md](RELEASING.md). You can set the
> credentials up now; uploads will not pass review until then.

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

1. **Android keystore** (steps 1–2) — needs no account, can be done immediately
2. **Apple Developer Program** — one enrolment unlocks both iOS and macOS
3. **App Store Connect API key** — shared by iOS and macOS notarization
4. **The two certificates** — Apple Distribution for iOS, Developer ID Application for macOS
5. **Google Play account** — cheapest, but the store is blocked until Avalonia 12 anyway

## First run of each workflow

Every one has a safe mode; use it before trusting a tag.

| Workflow | Safe first run |
| --- | --- |
| `publish-google-play.yml` | manual, `track: internal`, `status: draft` |
| `publish-app-store.yml` | manual — TestFlight is not the App Store |
| `publish-macos.yml` | manual — produces a **draft** GitHub Release |

None of these three have ever run. They are written from each action's documented input contract,
so expect the first run of each to need adjustment. For reference, the Microsoft Store workflow
took two attempts *with working credentials*.

## What is not needed

`APPLE_TEAM_ID` appears in [RELEASING.md](RELEASING.md) but is not referenced by any workflow.
Skip it unless something later asks for it.
