#!/usr/bin/env python3
"""Package reviewed linux-x64 multi-file publishes for the dt2prod installer."""

import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile

REPO = Path(__file__).resolve().parents[1]
ARCHIVE = "ue-dt-server-rhel84.tar.gz"


def normalized_scripts(stage):
    for source in (REPO / "packaging/linux/dt-setup").rglob("*"):
        if source.is_file():
            dest = stage / source.relative_to(REPO / "packaging/linux/dt-setup")
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(source.read_bytes().replace(b"\r\n", b"\n"))
    for name in ("dt-setup.sh", "dt-setup.py", "dt-setup-cron.py", "prepare-server-store.sh", "server-disk-usage.sh"):
        dest = stage / name
        dest.write_bytes((REPO / "scripts" / name).read_bytes().replace(b"\r\n", b"\n"))
        dest.chmod(0o755)


def valid_name(path):
    return all(re.fullmatch(r"[A-Za-z0-9_.+-]+", part) for part in path.parts)


def stage_payload(source, target, executable):
    if not source.is_dir() or source.is_symlink():
        raise ValueError("Expected an unlinked publish directory: {}".format(source))
    for entry in source.rglob("*"):
        relative = entry.relative_to(source)
        if not valid_name(relative) or entry.is_symlink() or not (entry.is_file() or entry.is_dir()):
            raise ValueError("Unexpected publish entry: {}".format(entry))
        if entry.is_file() and entry.suffix != ".pdb":
            dest = target / relative
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(str(entry), str(dest))
            dest.chmod(0o755 if relative == Path(executable) else 0o644)
    for name in (executable, executable + ".dll", executable + ".deps.json",
                 executable + ".runtimeconfig.json", "libcoreclr.so"):
        if not (target / name).is_file():
            raise ValueError("Multi-file self-contained publish is missing {}".format(name))
    with (target / executable).open("rb") as stream:
        if stream.read(4) != b"\x7fELF":
            raise ValueError("Expected a Linux ELF apphost: {}".format(executable))


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def audit(stage):
    checker = REPO / "tools/docker/prod-server/check-elf.sh"
    # Feed the normalized script so this also works with a Windows Git checkout in WSL.
    result = subprocess.run(["bash", "-s", "--", "--multi-only",
                             str(stage / "server-linux-x64"), str(stage / "linux-x64")],
                            input=checker.read_bytes().replace(b"\r\n", b"\n"),
                            stdout=subprocess.PIPE, env=dict(os.environ, LC_ALL="C"))
    summary = re.search(rb"^SUMMARY ELF=([1-9][0-9]*) sqlite=0 maxGLIBC=(2\.[0-9]+)$",
                        result.stdout, re.M)
    if result.returncode or not summary:
        print(result.stdout.decode("utf-8", errors="replace"))
        raise ValueError("Publish ELF verification failed.")
    record = "linux-x64 ELF={} maxGLIBC={}\n".format(
        summary.group(1).decode("ascii"), summary.group(2).decode("ascii"))
    (stage / "ABI.txt").write_text(record, encoding="ascii")
    print(record.strip())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="New or existing output directory")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="dt-setup-bundle-") as temporary:
        root = Path(temporary)
        stage = root / "ue-dt-server-rhel84"
        stage.mkdir()
        normalized_scripts(stage)
        stage_payload(args.server, stage / "server-linux-x64", "UeDtLauncher.DistributionServer")
        stage_payload(args.cli, stage / "linux-x64", "UeDtLauncher")
        audit(stage)
        inventory = sorted(path for path in stage.rglob("*") if path.is_file())
        manifest = "".join("{}  {}\n".format(sha256(path), path.relative_to(stage)) for path in inventory)
        (stage / "SHA256SUMS").write_text(manifest, encoding="ascii")
        archive = root / ARCHIVE
        with tarfile.open(str(archive), "w:gz") as output:
            output.add(str(stage), arcname=stage.name)
        destination = args.output / ARCHIVE
        with destination.open("xb") as output:
            try:
                with archive.open("rb") as source:
                    shutil.copyfileobj(source, output)
            except BaseException:
                destination.unlink()
                raise
        print("{}  {}".format(sha256(destination), destination))


if __name__ == "__main__":
    main()
