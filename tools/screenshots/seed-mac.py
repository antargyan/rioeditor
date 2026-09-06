"""Seed the macOS app the same way as iOS: a real file on disk plus settings."""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import docs

doc_name, theme = sys.argv[1], sys.argv[2]
home = os.path.expanduser('~')
doc_dir = os.path.join(home, 'Documents', 'RioEditor-Shots')
os.makedirs(doc_dir, exist_ok=True)
doc_path = os.path.join(doc_dir, 'Onboarding.md')
open(doc_path, 'w').write(getattr(docs, doc_name))

cfg = os.path.join(home, 'Library', 'Application Support', 'RioEditor', 'settings.json')
os.makedirs(os.path.dirname(cfg), exist_ok=True)
settings = {
    'theme': theme,
    'lastOpenedFile': doc_path,
    'windowWidth': 1440, 'windowHeight': 900, 'windowMaximized': False,
    'autosaveIntervalSeconds': 5,
    'wasm': {'persistDraftInBrowserStorage': True, 'allowRemoteScripts': True,
             'useDownloadFallbackForSave': True},
    'sponsor': {'launchCount': 1, 'activeDays': 1, 'saveCount': 0,
                'promptCount': 0, 'dismissed': True},
}
json.dump({'rio.settings': json.dumps(settings)}, open(cfg, 'w'), indent=2)
print(f'seeded {doc_name} ({theme})')
