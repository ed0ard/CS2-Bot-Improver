import argparse
import base64
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

from nacl.signing import VerifyKey


DISPLAY_VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)\.(\d+)(?:-Preview\.(\d+))?$")


def version_key(value: str) -> tuple[int, int, int, int, int, int]:
    match = DISPLAY_VERSION.fullmatch(value)
    if not match:
        raise SystemExit(f"Invalid display version: {value}")
    major, minor, patch, revision, preview = match.groups()
    return (
        int(major), int(minor), int(patch), int(revision),
        1 if preview is None else 0,
        0 if preview is None else int(preview),
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def verify_zip(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        if len(archive.infolist()) > 50_000:
            raise SystemExit(f"Too many ZIP entries: {path.name}")
        total = 0
        for info in archive.infolist():
            name = info.filename.replace("\\", "/")
            pure = PurePosixPath(name)
            if pure.is_absolute() or ".." in pure.parts or ":" in pure.parts[0]:
                raise SystemExit(f"Unsafe ZIP path in {path.name}: {info.filename}")
            total += info.file_size
            if total > 2 * 1024 * 1024 * 1024:
                raise SystemExit(f"Extracted ZIP size limit exceeded: {path.name}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--public-key", type=Path, required=True)
    args = parser.parse_args()

    manifest_path = args.directory / "latest.json"
    signature_path = args.directory / "latest.json.sig"
    document = manifest_path.read_bytes()
    signature = base64.b64decode(signature_path.read_text(encoding="ascii").strip(), validate=True)
    public_key = base64.b64decode(args.public_key.read_text(encoding="ascii").strip(), validate=True)
    VerifyKey(public_key).verify(document, signature)

    manifest = json.loads(document)
    if manifest.get("schema_version") != 1:
        raise SystemExit("Unsupported latest.json schema")
    release_version = manifest.get("release_version", "")
    version_key(release_version)
    for name in ("panel", "plugin"):
        component = manifest["components"][name]
        if component.get("version") != release_version:
            raise SystemExit(f"{name} version does not match release_version")
        minimum = component.get("min_panel_version", "")
        if version_key(minimum) >= version_key(component["version"]):
            raise SystemExit(
                f"{name} min_panel_version must accept at least one older Panel release"
            )
        asset = args.directory / component["url"].rsplit("/", 1)[-1]
        if not asset.is_file():
            raise SystemExit(f"Missing update asset: {asset.name}")
        if asset.stat().st_size != component["size"]:
            raise SystemExit(f"Size mismatch: {asset.name}")
        if sha256(asset) != component["sha256"]:
            raise SystemExit(f"SHA-256 mismatch: {asset.name}")
        verify_zip(asset)

    sums = {}
    for line in (args.directory / "SHA256SUMS.txt").read_text(encoding="ascii").splitlines():
        digest, name = line.split(None, 1)
        sums[name.strip()] = digest
    for path in args.directory.iterdir():
        if path.is_file() and path.name != "SHA256SUMS.txt":
            if sums.get(path.name) != sha256(path):
                raise SystemExit(f"SHA256SUMS mismatch: {path.name}")


if __name__ == "__main__":
    main()
