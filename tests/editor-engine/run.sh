#!/usr/bin/env bash
# Runs the editor engine tests in headless Chrome; exits non-zero if any test failed.
#
# A real browser rather than jsdom: the engine leans on Selection, Range and
# document.execCommand, none of which jsdom implements well enough for a pass to mean anything.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
port="${RIO_TEST_PORT:-8901}"
out="$(mktemp -d)"
server_pid=""
cleanup() {
  [ -n "$server_pid" ] && kill "$server_pid" 2>/dev/null
  pkill -f -- "--user-data-dir=$out/profile" 2>/dev/null
  # Chrome can still be flushing its profile as we tear down; the temp dir is disposable either way.
  sleep 0.3
  rm -rf "$out" 2>/dev/null
}
trap cleanup EXIT

chrome="${CHROME:-}"
if [ -z "$chrome" ]; then
  for candidate in \
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
    google-chrome chromium-browser chromium; do
    if [ -x "$candidate" ] || command -v "$candidate" >/dev/null 2>&1; then chrome="$candidate"; break; fi
  done
fi
[ -n "$chrome" ] || { echo "No Chrome or Chromium found; set CHROME to its path." >&2; exit 1; }

python3 -m http.server "$port" --directory "$repo_root" >/dev/null 2>&1 &
server_pid=$!
for _ in $(seq 1 50); do
  curl -fsS -o /dev/null "http://localhost:$port/tests/editor-engine/runner.html" 2>/dev/null && break
  sleep 0.2
done

# Chrome is backgrounded and bounded rather than waited on: --dump-dom does not reliably exit
# on its own, and a test runner that can hang forever is worse than one that fails.
"$chrome" --headless=new --disable-gpu --no-sandbox --no-first-run --no-default-browser-check \
  --user-data-dir="$out/profile" --virtual-time-budget=10000 \
  --dump-dom "http://localhost:$port/tests/editor-engine/runner.html" > "$out/dom.html" 2>/dev/null &
for _ in $(seq 1 60); do
  [ -s "$out/dom.html" ] && grep -q '</title>' "$out/dom.html" 2>/dev/null && break
  sleep 0.5
done

title="$(grep -o '<title>[^<]*</title>' "$out/dom.html" 2>/dev/null | head -1 | sed 's|</\{0,1\}title>||g')"
[ -n "$title" ] || { echo "The test page did not run (no title in the dumped DOM)." >&2; exit 1; }
echo "editor engine: $title"

case "$title" in
  FAIL*)
    echo "--- failures ---"
    grep -o '<div class="fail">[^<]*' "$out/dom.html" | sed 's|<div class="fail">||'
    exit 1 ;;
esac
exit 0
