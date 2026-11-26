"""Create ZIPs from generated files or the read-only staged Windows package."""

import hashlib
import json
import sys
import zipfile
from pathlib import Path


def pack(project, destination):
    root = Path("/bench-static") if project != "m7at10-dt" else Path("/ue")
    if project == "m7at10-dt":
        members = [(path, path.relative_to(root).as_posix()) for path in sorted(root.rglob("*"))
                   if path.is_file()]
        if sum(path.stat().st_size for path, _ in members) != 7_122_065_711:
            raise ValueError("Expected the exact staged m7at10 payload size")
    else:
        members = [(root / f'{project.rsplit("-", 1)[-1]}.bin', "payload.bin"),
                   (Path("/game.exe"), "game.exe")]
    if any(path.is_symlink() for path in root.rglob("*")):
        raise ValueError("Packaging does not follow links")
    destination.mkdir()
    (destination / "transfer.uploading").touch()
    payload = destination / "payload.zip"
    with zipfile.ZipFile(payload, "x", zipfile.ZIP_STORED) as archive:
        for path, member in members:
            archive.write(path, member)
    return {"payloadBytes": sum(path.stat().st_size for path, _ in members),
            "files": len(members), "zipBytes": payload.stat().st_size}


def ue_archive_inventory(payload):
    evidence = {}
    with zipfile.ZipFile(payload) as archive:
        for item in archive.infolist():
            if item.filename in evidence:
                raise ValueError("Duplicate archive member")
            with archive.open(item) as member:
                evidence[item.filename] = [item.file_size, hashlib.file_digest(member, "sha256").hexdigest()]
    return evidence


if __name__ == "__main__":
    operation, target = sys.argv[1], Path(sys.argv[2])
    evidence = ue_archive_inventory(target) if operation == "ue-inventory" else pack(operation, target)
    print(json.dumps(evidence))
