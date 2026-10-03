import hashlib
import json
import re
import shutil
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
UPLOADER_VERSION = "0.2.0"
UPLOADER_SHA256 = "5b29c94bff029cd308aebd78ee153224ad16b5e9ad969c7739c128dd0d1bc2d9"


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def read_config(root):
    config = json.loads((root / "workshop/config.json").read_text())
    item_id = config["id"]
    if item_id is not None and (type(item_id) is not int or not 0 < item_id < 2**64):
        raise ValueError("Invalid Workshop item ID")
    if config["visibility"] not in ("public", "private", "unlisted", "friends_only"):
        raise ValueError("Invalid Workshop visibility")
    return config


def set_version(root, version):
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", version):
        raise ValueError("Version must use major.minor.patch, for example 0.3.1")
    project = root / "src/spp.csproj"
    source, count = re.subn(r"<Version>[^<]*</Version>", f"<Version>{version}</Version>", project.read_text())
    if count != 1:
        raise ValueError("Expected one Version property in src/spp.csproj")
    manifest_path = root / "src/spp.json"
    manifest = json.loads(manifest_path.read_text())
    manifest["version"] = version
    project.write_text(source)
    write_json(manifest_path, manifest)


def prepare(root):
    config = read_config(root)
    manifest = json.loads((root / "src/spp.json").read_text())
    version = ET.parse(root / "src/spp.csproj").findtext(".//Version")
    if manifest["version"] != version:
        raise ValueError("Versions in src/spp.json and src/spp.csproj must match")
    image = root / "workshop/image.png"
    if not image.is_file() or image.stat().st_size >= 1_000_000:
        raise ValueError("workshop/image.png must exist and be smaller than 1 MB")
    if not image.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("workshop/image.png must be a PNG")
    package = root / "artifacts/dist/spp"
    if json.loads((package / "spp.json").read_text()) != manifest:
        raise ValueError("Packaged manifest is stale; build the current source first")
    workspace = root / "artifacts/workshop"
    workspace.mkdir(parents=True, exist_ok=True)
    item_file = workspace / "mod_id.txt"
    if item_file.exists() and int(item_file.read_text().strip()) != config["id"]:
        raise ValueError("Existing Workshop ID differs from workshop/config.json")
    if config["id"] is not None:
        item_file.write_text(str(config["id"]) + "\n")
    content = workspace / "content"
    if content.exists():
        shutil.rmtree(content)
    (content / "spp").mkdir(parents=True)
    for name in ("spp.dll", "spp.json", "LICENSE"):
        shutil.copy2(package / name, content / "spp" / name)
    shutil.copy2(image, workspace / "image.png")
    write_json(workspace / "workshop.json", {
        "title": manifest["name"],
        "description": config["description"],
        "visibility": config["visibility"],
        "changeNote": "Version " + version,
        "dependencies": config["dependencies"],
        "tags": [],
        "contentDescriptors": [],
    })
    previews = workspace / "previews"
    images = sorted((root / "workshop/previews").glob("*.png"))
    if not images:
        raise ValueError("Workshop previews are missing")
    for preview in images:
        if preview.stat().st_size >= 1_000_000 or not preview.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError(f"{preview.name} must be a PNG smaller than 1 MB")
    if previews.exists():
        shutil.rmtree(previews)
    previews.mkdir()
    for preview in images:
        shutil.copy2(preview, previews / preview.name)
    return workspace


def record(root):
    config = read_config(root)
    workspace = root / "artifacts/workshop"
    item_file = workspace / "mod_id.txt"
    ids = set()
    if item_file.is_file():
        ids.add(int(item_file.read_text().strip()))
    log = workspace / "mod-uploader.log"
    if log.is_file():
        ids.update(int(value) for value in re.findall(r"Uploading .* with item ID (\d+)\.\.\.", log.read_text()))
    if not ids:
        return
    if len(ids) != 1 or not 0 < next(iter(ids)) < 2**64:
        raise ValueError("Uploader returned conflicting Workshop item IDs")
    item_id = ids.pop()
    if config["id"] is not None and config["id"] != item_id:
        raise ValueError("Uploader returned a different Workshop item ID")
    config["id"] = item_id
    write_json(root / "workshop/config.json", config)
    item_file.write_text(str(item_id) + "\n")


def uploader(root):
    tools = root / "artifacts/tools"
    tools.mkdir(parents=True, exist_ok=True)
    archive = tools / f"ModUploader-{UPLOADER_VERSION}-linux-x64.zip"
    if not archive.exists():
        url = f"https://github.com/megacrit/sts2-mod-uploader/releases/download/v{UPLOADER_VERSION}/ModUploader-linux-x64.zip"
        temporary = archive.with_suffix(".download")
        with urllib.request.urlopen(url, timeout=60) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        temporary.replace(archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != UPLOADER_SHA256:
        raise ValueError("Official uploader checksum mismatch")
    destination = tools / f"uploader-{UPLOADER_VERSION}"
    destination.mkdir(exist_ok=True)
    with zipfile.ZipFile(archive) as source:
        for name in ("ModUploader", "libsteam_api.so", "steam_appid.txt"):
            (destination / name).write_bytes(source.read(name))
    executable = destination / "ModUploader"
    executable.chmod(0o755)
    return executable


if __name__ == "__main__":
    try:
        command = sys.argv[1] if len(sys.argv) > 1 else ""
        if command == "prepare":
            print(prepare(ROOT))
        elif command == "record":
            record(ROOT)
        elif command == "uploader":
            print(uploader(ROOT))
        elif command == "version" and len(sys.argv) == 3:
            set_version(ROOT, sys.argv[2])
        else:
            raise ValueError("Usage: workshop.py prepare|record|uploader|version <major.minor.patch>")
    except (OSError, ValueError, KeyError) as error:
        sys.exit(str(error))
