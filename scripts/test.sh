#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
spp_dotnet run --project tests/unit/StatsTests.csproj -c Release
case "${1:-}" in
  --source)
    [[ "$#" == 1 ]] || { echo 'Usage: ./scripts/test.sh [--source|game-directory]' >&2; exit 2; }
    python3 tests/check_package.py --source
    ;;
  --*) echo 'Usage: ./scripts/test.sh [--source|game-directory]' >&2; exit 2 ;;
  *)
    [[ "$#" -le 1 ]] || { echo 'Usage: ./scripts/test.sh [--source|game-directory]' >&2; exit 2; }
    ./scripts/build.sh "${1:-}"
    python3 tests/check_package.py
    ;;
esac
