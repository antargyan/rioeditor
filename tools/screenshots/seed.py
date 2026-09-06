"""Seed the app with a real document on disk, so screenshots show a named file
("Onboarding.md") rather than an Untitled draft."""
import json, os, subprocess, sys

udid, bundle, doc_name, theme = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import docs

container = subprocess.check_output(
    ['xcrun', 'simctl', 'get_app_container', udid, bundle, 'data']).decode().strip()

# A real file: session restore prefers LastOpenedFile over the draft, which makes the
# title bar and status line show a named document.
doc_path = os.path.join(container, 'Documents', 'Onboarding.md')
os.makedirs(os.path.dirname(doc_path), exist_ok=True)
open(doc_path, 'w').write(getattr(docs, doc_name))

path = os.path.join(container, 'Documents', '.config', 'RioEditor', 'settings.json')
os.makedirs(os.path.dirname(path), exist_ok=True)

settings = {
    'theme': theme,
    'lastOpenedFile': doc_path,
    'autosaveIntervalSeconds': 5,
    'wasm': {'persistDraftInBrowserStorage': True, 'allowRemoteScripts': True,
             'useDownloadFallbackForSave': True},
    # Dismissed so the sponsorship banner can never appear in a store screenshot.
    'sponsor': {'launchCount': 1, 'activeDays': 1, 'saveCount': 0,
                'promptCount': 0, 'dismissed': True},
}
json.dump({'rio.settings': json.dumps(settings)}, open(path, 'w'), indent=2)
print(f'seeded {doc_name} ({theme})')
