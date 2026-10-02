#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
spp_game_paths "${1:-}"
project_dir="$PWD"
sandbox_dir="$project_dir/artifacts/sandbox"
spp_dotnet build src/spp.csproj -c Release "-p:Sts2DataDir=$data_dir" -p:SppSelfTest=true "-p:ArtifactsPath=$project_dir/artifacts/integration"
python3 - "$game_dir" "$sandbox_dir" "$project_dir" <<'PY'
import json, shutil, sys
from pathlib import Path
source, sandbox, project = map(Path, sys.argv[1:])
marker = sandbox / '.spp-sandbox'
if sandbox.exists() and any(sandbox.iterdir()) and not marker.is_file():
    raise SystemExit('Test sandbox must be empty or marked for spp.')
sandbox.mkdir(parents=True, exist_ok=True)
marker.touch()
game = sandbox / 'game'
game.mkdir(exist_ok=True)
for item in source.iterdir():
    target = game / item.name
    if item.name in ('mods', 'steam_appid.txt') or target.exists(): continue
    if item.name == 'SlayTheSpire2': shutil.copy2(item, target)
    else: target.symlink_to(item.resolve())
shutil.copytree(project / 'artifacts/integration/dist/spp', game / 'mods/spp', dirs_exist_ok=True)
user = sandbox / 'userdata/SlayTheSpire2'
settings = user / 'default/1/settings.save'
settings.parent.mkdir(parents=True, exist_ok=True)
settings.write_text(json.dumps({'schema_version': 8, 'mod_settings': {'mods_enabled': True, 'mod_list': []},
    'volume_master': 0, 'skip_intro_logo': True, 'seen_ea_disclaimer': True, 'fullscreen': False, 'fps_limit': 60, 'language': 'eng'}))
(user / '.spp-test-sandbox').touch()
(user / 'spp-selftest.json').unlink(missing_ok=True)
PY
command -v Xvfb >/dev/null || { echo 'Xvfb is required.' >&2; exit 1; }
mkdir -p artifacts/validation
display_file="$sandbox_dir/display"
: > "$display_file"
Xvfb -displayfd 3 -screen 0 1280x720x24 -nolisten tcp 3> "$display_file" > artifacts/validation/display.log 2>&1 &
display_pid=$!
trap 'kill "$display_pid" 2>/dev/null || true; wait "$display_pid" 2>/dev/null || true' EXIT
for ((attempt=0; attempt<100; attempt++)); do
  [[ -s "$display_file" ]] && break
  kill -0 "$display_pid" 2>/dev/null || { echo 'Xvfb failed.' >&2; exit 1; }
  sleep 0.1
done
[[ -s "$display_file" ]] || { echo 'Xvfb did not become ready.' >&2; exit 1; }
DISPLAY=":$(< "$display_file")"
export DISPLAY
unset WAYLAND_DISPLAY
runner=()
if command -v steam-run >/dev/null; then runner=(steam-run); fi
LP_NUM_THREADS=4 XDG_DATA_HOME="$sandbox_dir/userdata" timeout --kill-after=10 300 "${runner[@]}" "$sandbox_dir/game/SlayTheSpire2" --audio-driver Dummy --force-steam=off --spp-selftest --display-driver x11 --rendering-method gl_compatibility --rendering-driver opengl3 --windowed --resolution 1280x720 > artifacts/validation/game.log 2>&1
cp "$sandbox_dir/userdata/SlayTheSpire2/spp-selftest.json" artifacts/validation/game.json
python3 - <<'PY'
import json
from pathlib import Path
report = json.loads(Path('artifacts/validation/game.json').read_text())
if not report['success']: raise SystemExit(report['error'])
print(f"PASS {len(report['passed'])} isolated game checks")
PY
cp "$sandbox_dir/userdata/SlayTheSpire2/spp-card.png" artifacts/validation/card.png
cp "$sandbox_dir/userdata/SlayTheSpire2/spp-relic.png" artifacts/validation/relic.png
cp "$sandbox_dir/userdata/SlayTheSpire2/spp-shop-card.png" artifacts/validation/shop-card.png
