#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/common.sh
spp_game_paths "${1:-}"
spp_dotnet build src/spp.csproj -c Release "-p:Sts2DataDir=$data_dir" "-p:BaseLibDll=$baselib"
python3 - <<'PY'
import hashlib, json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
package = Path('artifacts/dist') / f"spp-{json.loads(Path('src/spp.json').read_text())['version']}.zip"
with ZipFile(package, 'w', ZIP_DEFLATED) as archive:
    for file in [Path('artifacts/dist/spp/spp.dll'), Path('artifacts/dist/spp/spp.json'), Path('artifacts/dist/spp/LICENSE')]:
        archive.write(file, 'spp/' + file.name)
files = [Path('artifacts/dist/spp/spp.dll'), package]
Path('artifacts/dist/SHA256SUMS').write_text(''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to("artifacts/dist")}\n' for p in files))
print(f'Package: {package}')
PY
