import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    manifest_path = root / 'src/spp.json'
    manifest = json.loads(manifest_path.read_text())
    project = ET.parse(root / 'src/spp.csproj')
    version = project.findtext('./PropertyGroup/Version')
    require(manifest['id'] == 'spp', 'Unexpected mod identity.')
    require(manifest['version'] == version, 'Manifest and assembly versions differ.')
    require(re.fullmatch(r'\d+\.\d+\.\d+', version or ''), 'Invalid release version.')
    require(manifest['has_dll'] and not manifest['has_pck'], 'Unexpected package format.')
    for reference in project.findall('./ItemGroup/Reference'):
        require(reference.findtext('Private') == 'false', 'Runtime dependencies must remain external.')
    if args.source:
        print('PASS source package configuration')
        return
    dist = root / 'artifacts/dist'
    package = dist / f'spp-{version}.zip'
    expected = {'spp/spp.dll', 'spp/spp.json', 'spp/LICENSE'}
    with ZipFile(package) as archive:
        require(len(archive.namelist()) == len(expected) and set(archive.namelist()) == expected,
                'Unexpected release contents.')
        require(archive.testzip() is None, 'Archive integrity check failed.')
        for name in expected:
            require(archive.read(name) == (dist / name).read_bytes(), f'Packaged file differs: {name}')
        require(archive.read('spp/spp.json') == manifest_path.read_bytes(), 'Packaged manifest is stale.')
        require(archive.read('spp/LICENSE') == (root / 'LICENSE').read_bytes(), 'Packaged license is stale.')
        require(archive.read('spp/spp.dll').startswith(b'MZ'), 'Missing .NET assembly.')
    hashes = dict(line.split('  ', 1)[::-1] for line in (dist / 'SHA256SUMS').read_text().splitlines())
    require(set(hashes) == {'spp/spp.dll', package.name}, 'Unexpected checksum entries.')
    for name, digest in hashes.items():
        require(hashlib.sha256((dist / name).read_bytes()).hexdigest() == digest, f'Checksum mismatch: {name}')
    print('PASS source configuration, release contents and checksums')


if __name__ == '__main__':
    main()
