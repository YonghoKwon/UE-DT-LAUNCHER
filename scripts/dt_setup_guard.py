#!/usr/bin/env python3
"""Guard the distribution process and recognize the installed authority."""

import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import time

BASE = Path("/dt")
CONTROL = BASE / ".dt-setup"
STATE = CONTROL / "state.json"
PENDING = CONTROL / "pending.json"
CURRENT = BASE / "publish/dt-current"
VERSIONS = BASE / "publish/dt-versions"
SERVER = "server-linux-x64/UeDtLauncher.DistributionServer"
STORE = BASE / "store"
HEAD = STORE / "authority/HEAD"
SHA = re.compile(r"[a-f0-9]{64}")
UUID = re.compile(r"[a-f0-9]{12}4[a-f0-9]{3}[89ab][a-f0-9]{15}")


class SetupError(Exception):
    pass


def need(condition, message):
    if not condition:
        raise SetupError(message)


def identity(path, kind):
    info = path.lstat()
    need(info.st_uid == os.getuid() and kind(info.st_mode),
         "Unexpected owner or type: {}".format(path))
    return [info.st_dev, info.st_ino]


def version_path(value):
    return isinstance(value, str) and re.fullmatch(
        re.escape(str(VERSIONS)) + r"/release-[a-f0-9]{64}", value) is not None


def unique_object(pairs):
    obj = dict(pairs)
    need(len(obj) == len(pairs), "Duplicate JSON entries.")
    return obj


def read_control(path):
    identity(path, stat.S_ISREG)
    need(path.resolve() == path, "Linked control path: {}".format(path))
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def recognized_store(expected=None):
    # HEAD changes on every authority publish; bind stable directories and
    # validate the current, checksummed HEAD instead of pinning a generation.
    found = [identity(STORE, stat.S_ISDIR), identity(HEAD.parent, stat.S_ISDIR)]
    need(expected is None or found == expected, "Installed authority directory identity changed.")
    need(HEAD.resolve() == HEAD, "Authority HEAD path contains a link.")
    for _ in range(3):
        identity(HEAD, stat.S_ISREG)
        with HEAD.open("rb") as stream:
            raw = stream.read(65537)
        need(len(raw) <= 65536, "Authority HEAD is oversized.")
        head = json.loads(raw, object_pairs_hook=unique_object)
        fields = ("schema", "generation", "files", "sha256", "previous", "headSha256")
        need(isinstance(head, dict) and tuple(head) == fields and
             type(head["schema"]) is int and head["schema"] == 1 and
             type(head["generation"]) is int and 0 <= head["generation"] <= 2**63 - 1,
             "Invalid authority HEAD schema.")
        generation = head["generation"]
        need((head["previous"] is None if generation == 0 else
              type(head["previous"]) is int and head["previous"] == generation - 1),
             "Invalid authority HEAD predecessor.")
        signature = head["headSha256"]
        need(isinstance(signature, str) and SHA.fullmatch(signature), "Invalid authority HEAD checksum.")
        head["headSha256"] = ""
        compact = lambda value: json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("ascii")
        need(hashlib.sha256(compact(head)).hexdigest() == signature, "Invalid authority HEAD checksum.")
        head["headSha256"] = signature
        need(raw == compact(head), "Authority HEAD is not canonical.")
        areas = {"catalog", "jobs", "auth", "sequence", "events"}
        refs, hashes = head["files"], head["sha256"]
        need(isinstance(refs, dict) and isinstance(hashes, dict) and
             set(refs) == set(hashes) == areas, "Invalid authority HEAD references.")
        missing = False
        for area in areas:
            value, checksum = refs[area], hashes[area]
            match = isinstance(value, str) and re.fullmatch(area + r"/([0-9]{6,19})\.json", value)
            need(match and isinstance(checksum, str) and SHA.fullmatch(checksum),
                 "Invalid authority HEAD reference.")
            index = int(match[1])
            need(value == "{}/{:06d}.json".format(area, index) and index <= generation and
                 (area != "events" or index == generation), "Invalid authority HEAD generation reference.")
            path = HEAD.parent / value
            try:
                identity(path, stat.S_ISREG)
                need(path.resolve() == path, "Linked authority generation.")
                digest = hashlib.sha256()
                with path.open("rb") as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        digest.update(chunk)
                need(digest.hexdigest() == checksum, "Authority generation checksum differs.")
            except FileNotFoundError:
                missing = True
                break
        if not missing:
            return found
        # A live writer may retire an older reference after we read HEAD.
        if HEAD.read_bytes() == raw:
            break
        time.sleep(0.02)
    raise SetupError("Authority HEAD references are missing.")


def process_stamp(pid):
    proc = Path("/proc") / str(pid)
    need(type(pid) is int and pid > 1 and proc.stat().st_uid == os.getuid(), "Invalid installer process.")
    fields = (proc / "stat").read_text().rsplit(")", 1)[1].split()
    need(fields[0] != "Z", "Installer process exited.")
    return [pid, fields[19], Path("/proc/sys/kernel/random/boot_id").read_text().strip()]


def allow_start():
    identity(CONTROL, stat.S_ISDIR)
    need(CONTROL.resolve() == CONTROL, "Linked control directory.")
    selection = None
    if os.path.lexists(str(PENDING)):
        pending = read_control(PENDING)
        need(isinstance(pending, dict), "Invalid pending startup record.")
        permit = pending.get("start")
        fd = os.open(str(BASE), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                if isinstance(permit, dict):
                    need(permit["owner"] == process_stamp(permit["owner"][0]),
                         "Pending transaction startup authorization expired.")
                    selection = permit
        finally:
            os.close(fd)
        need(selection is not None, "Recover the pending transaction with install before starting.")
    else:
        selection = read_control(STATE)
    need(isinstance(selection, dict), "Invalid installed startup record.")
    target = selection.get("active")
    need(version_path(target) and CURRENT.is_symlink() and
         os.readlink(str(CURRENT)) == target, "Installed publish selection differs.")
    identity(Path(target), stat.S_ISDIR)
    need(Path(target).resolve() == Path(target), "Installed publish path contains a link.")
    need(selection.get("store_id") is not None, "Installed authority identity is missing.")
    recognized_store(selection["store_id"])


def main():
    try:
        allow_start()
        executable = str(CURRENT / SERVER)
        os.execv(executable, [executable, "serve", "--config", "/dt/config/server.json"])
    except (SetupError, OSError, ValueError, KeyError, TypeError, IndexError) as error:
        print("FAIL {}".format(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
