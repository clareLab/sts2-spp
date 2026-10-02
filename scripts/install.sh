#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
spp_game_paths "${1:-}"
python3 tests/check_package.py
destination="$game_dir/mods/spp"
if [[ -d "$destination" ]]; then
  backup="artifacts/backups/spp-$(date +%Y%m%d-%H%M%S)"
  mkdir -p artifacts/backups
  cp -a "$destination" "$backup"
fi
mkdir -p "$destination"
cp artifacts/dist/spp/spp.dll artifacts/dist/spp/spp.json artifacts/dist/spp/LICENSE "$destination/"
echo "Installed: $destination"
echo 'Enable Stats ++ in the game Mod menu after restarting.'
