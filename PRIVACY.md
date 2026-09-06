# Privacy Policy for RioEditor

**Last updated: 6 September 2026**

RioEditor is a Markdown editor published by Antargyan. This policy describes exactly what the app does with your information. It is short because the app does very little.

## The short version

**RioEditor does not collect, transmit, or sell any personal information.** There are no accounts, no sign-in, no analytics, no telemetry, no advertising, and no third-party tracking of any kind. Your documents stay on your device.

## Your documents

Documents you write stay where you put them: on your device's file system, or — in the web version — in your browser's local storage. RioEditor never uploads, transmits, or reads them for any purpose other than showing them to you and saving them where you asked.

The app can only open and save files you explicitly choose through your system's own file picker. It does not scan or index your storage.

## What is stored on your device

RioEditor keeps a small settings file on your device (or, in the web version, in browser local storage). It contains:

- your chosen theme (light or dark)
- the path of the last document you opened, so it can be reopened next time
- window size and position
- the autosave interval
- an unsaved draft, so that work is not lost if the app closes unexpectedly
- counters used to decide whether to mention sponsorship: how many times the app has been launched, on how many separate days, how many times a document has been saved, and whether the sponsorship message has been shown or dismissed

**None of this leaves your device.** The counters exist solely so the app can avoid asking an occasional user to sponsor it; they are never transmitted anywhere, and they identify nothing about you. Deleting the app, or clearing site data in the web version, removes all of it.

## Network access

RioEditor works fully offline. It makes exactly one kind of network request, and only to render two optional things:

- **Mathematical notation** is rendered by KaTeX, and **diagrams** by Mermaid. Both are fetched from the public CDN `cdn.jsdelivr.net` when a document contains them.

As with any request to any website, the CDN receives your IP address and standard request information. RioEditor sends it nothing else — not your document, not your settings, not an identifier.

**You can turn this off.** Set `AllowRemoteScripts` to `false` in the app's settings file, and RioEditor makes no network requests at all. Maths and diagrams then appear as plain text.

Separately, if you click a link inside a document, or the "Sponsor" button, your normal web browser opens that address. What happens next is governed by that site's own privacy policy, not this one.

## Children

RioEditor is a text editor suitable for all ages. It collects nothing from anyone, including children.

## Data requests

Because no personal data is collected or transmitted, there is nothing for us to disclose, correct, export, or delete on request. Everything the app stores is already under your control on your own device.

## Changes to this policy

If this policy changes, the revised version will be published at this address with an updated date. Material changes will also be noted in the app's release notes.

## Contact

Questions about this policy: open an issue at <https://github.com/antargyan/rioeditor/issues>, or write to the address listed on the developer's App Store listing.

The app is open source. If you would rather verify these claims than take our word for them, the entire source is at <https://github.com/antargyan/rioeditor>.
