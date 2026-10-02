#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mode=upload
if [[ "${1:-}" == --prepare ]]; then mode=--prepare; shift; fi
[[ "$#" -le 1 ]] || { echo 'Usage: ./scripts/publish.sh [--prepare] [major.minor.patch]' >&2; exit 2; }
[[ "$(uname -sm)" == 'Linux x86_64' ]] || { echo 'This publisher requires Linux x86_64.' >&2; exit 1; }
mkdir -p artifacts
exec 9> artifacts/publish.lock
flock -n 9 || { echo 'Another publication is already running.' >&2; exit 1; }
if [[ "$#" == 1 ]]; then python3 scripts/workshop.py version "$1"; fi
./scripts/lint.sh
./scripts/test.sh
./scripts/test-game.sh
workspace="$(python3 scripts/workshop.py prepare)"
uploader="$(python3 scripts/workshop.py uploader)"
runner=()
if command -v steam-run >/dev/null; then runner=(steam-run); fi
if [[ "$mode" == --prepare ]]; then
  (cd "$workspace"; "${runner[@]}" "$uploader" --help >/dev/null)
  echo "Ready: $workspace"
  echo 'No Workshop item was created or updated.'
  exit 0
fi
result=0
rm -f "$workspace/mod-uploader.log"
trap 'python3 scripts/workshop.py record' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
(
  cd "$workspace"
  SteamAppId=2868840 "${runner[@]}" "$uploader" upload -w "$workspace"
) || result=$?
python3 scripts/workshop.py record
trap - EXIT INT TERM
[[ "$result" == 0 ]] || exit "$result"
python3 - <<'PY'
import json
from pathlib import Path
item_id = json.loads(Path('workshop/config.json').read_text())['id']
if item_id is None:
    raise SystemExit('Uploader did not return a Workshop item ID.')
print(f'Published: https://steamcommunity.com/sharedfiles/filedetails/?id={item_id}')
PY
