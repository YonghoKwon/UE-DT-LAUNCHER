#!/usr/bin/env python3
"""Unprivileged installer for the port-80 request-signature deployment."""

import argparse
import base64
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import uuid
from dt_setup_guard import (SetupError, UUID, identity, need, process_stamp,
                            read_control, recognized_store, version_path)

BASE = Path("/dt")
CONTROL = BASE / ".dt-setup"
STATE = CONTROL / "state.json"
PENDING = CONTROL / "pending.json"
CURRENT = BASE / "publish/dt-current"
VERSIONS = BASE / "publish/dt-versions"
LEGACY = BASE / ".dt-legacy"
UNIT_NAME = "ue-dt-distribution.service"
UNIT = Path.home() / ".config/systemd/user" / UNIT_NAME
RUNNER = BASE / "tools/dt-setup-cron.py"
GUARD = BASE / "tools/dt_setup_guard.py"
CRON_JOB = "@reboot /usr/bin/python3 -B " + str(RUNNER) + " boot # uedt-dt-setup"
SERVER = "server-linux-x64/UeDtLauncher.DistributionServer"
LAUNCHER = "linux-x64/UeDtLauncher"
CATALOG = "/api/v1/catalog"
LOOPBACK = "http://127.0.0.1:18500"
BUNDLE = Path(__file__).resolve().parent


def command(*args, **kwargs):
    return subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def output(*args):
    return command(*args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                   universal_newlines=True).stdout.strip()


def succeeds(*args):
    return subprocess.run(args, stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL).returncode == 0


def exists(path):
    return os.path.lexists(str(path))


def sync_dir(path):
    fd = os.open(str(path), os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_write(path, contents):
    fd, name = tempfile.mkstemp(prefix=".dt-setup-", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, str(path))
        sync_dir(path.parent)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def write_json(path, value):
    atomic_write(path, json.dumps(value, indent=2) + "\n")


def read_json(path, default=None):
    if not exists(path):
        return default
    need(path.is_file() and not path.is_symlink(), "Expected a regular file at {}".format(path))
    return json.loads(path.read_text(encoding="utf-8"))


def config_for(path):
    # Parse JSON data. A comment may appear on its own line in the example.
    raw = "\n".join(line for line in path.read_text(encoding="utf-8").splitlines()
                    if not line.lstrip().startswith("#"))
    config = json.loads(raw)
    need(isinstance(config, dict) and set(config) == {
        "publicUrl", "signingKeyId", "nginxMode", "legacyProbe"
    }, "Review the four fields in dt-setup.conf.example.")
    for key, value in config.items():
        need(isinstance(value, str), "{} must be text.".format(key))
    need(re.fullmatch(r"http://[a-zA-Z0-9][a-zA-Z0-9.-]*(?::80)?", config["publicUrl"]),
         "Set publicUrl to the approved HTTP origin on port 80.")
    config["publicUrl"] = re.sub(r":80$", "", config["publicUrl"])
    host = config["publicUrl"][7:].lower().rstrip(".")
    need(host != "localhost" and not host.startswith("127.") and host != "0.0.0.0",
         "Use the approved public host name or address.")
    need(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}", config["signingKeyId"]),
         "Invalid signingKeyId.")
    need(config["nginxMode"] in ("coexist", "cutover"), "Use coexist or cutover.")
    need(re.fullmatch(r"/[A-Za-z0-9_./-]+", config["legacyProbe"]) and
         all(part not in ("", ".", "..") for part in config["legacyProbe"][1:].split("/")),
         "legacyProbe must be a public legacy file path.")
    return config


def server_config(config):
    return {
        "root": str(BASE / "store"), "publicUrl": config["publicUrl"], "listenUrl": LOOPBACK,
        "authenticationMode": "request-signature-v1",
        "signingKeyPath": str(BASE / "keys/signing.pem"), "signingKeyId": config["signingKeyId"],
        "policyPath": str(BASE / "config/access-policy.json"),
        "limits": {"maxZipBytes": 21474836480, "maxExpandedBytes": 22548578304, "maxFiles": 250000},
    }


def digest(path):
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(chunk)
    return sha.hexdigest()


def payload_files(directory):
    need(directory.is_dir() and not directory.is_symlink(), "Missing publish directory: {}".format(directory))
    for parent, directories, files in os.walk(str(directory), followlinks=False):
        for name in directories + files:
            entry = Path(parent) / name
            mode = entry.lstat().st_mode
            need(not entry.is_symlink() and (stat.S_ISDIR(mode) or stat.S_ISREG(mode)),
                 "Unexpected publish entry: {}".format(entry))
            if stat.S_ISREG(mode):
                yield entry


def bundle_info():
    manifest = BUNDLE / "SHA256SUMS"
    entries = {}
    for line in manifest.read_text(encoding="ascii").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9_.+/-]+)", line)
        need(match, "Malformed SHA256SUMS.")
        sha, relative = match.groups()
        need(all(part not in ("", ".", "..") for part in relative.split("/")) and
             relative not in entries, "Unsafe or duplicate SHA256SUMS path.")
        file = BUNDLE / relative
        need(file.is_file() and file.resolve() == file and digest(file) == sha,
             "Bundle hash mismatch: {}".format(relative))
        entries[relative] = sha
    required = {"dt-setup.sh", "dt-setup.py", "dt-setup-cron.py", "dt_setup_guard.py", "dt-setup.conf.example",
                "prepare-server-store.sh", "server-disk-usage.sh", UNIT_NAME, "ABI.txt", SERVER, LAUNCHER,
                "nginx/uedt-signed-proxy.inc", "nginx/coexist/ue-dt-updates.conf",
                "nginx/cutover/ue-dt-updates.conf"}
    for relative in ("server-linux-x64", "linux-x64"):
        required.update(str(path.relative_to(BUNDLE)) for path in payload_files(BUNDLE / relative))
    need(required <= entries.keys(), "Bundle manifest is missing required entries.")
    need(os.access(str(BUNDLE / SERVER), os.X_OK) and os.access(str(BUNDLE / LAUNCHER), os.X_OK),
         "Bundle executable permissions are missing.")
    abi = (BUNDLE / "ABI.txt").read_text(encoding="ascii")
    need(re.fullmatch(r"linux-x64 ELF=[1-9][0-9]* maxGLIBC=2\.([0-9]+)\n", abi) and
         int(abi.rstrip().rsplit(".", 1)[1]) <= 28, "Bundle has no compatible ABI record.")
    return digest(manifest), entries


def listener(port):
    return output("ss", "-H", "-ltn", "sport = :{}".format(port))


def curl_code(url):
    return output("curl", "--noproxy", "*", "-sS", "--max-time", "4", "-o", "/dev/null",
                  "-w", "%{http_code}", url)


def signed_challenge(endpoint, origin):
    probe = "dt-setup-probe"
    body = output("curl", "--noproxy", "*", "-fsS", "--max-time", "4", "--max-filesize", "4096",
                  endpoint + "/api/v1/auth/challenge?keyId=" + probe)
    document = json.loads(body)
    need(isinstance(document, dict) and set(document) == {"challenge", "expiresInSeconds"} and
         type(document["expiresInSeconds"]) is int and document["expiresInSeconds"] == 60 and
         isinstance(document["challenge"], str) and len(document["challenge"]) <= 2048,
         "Unexpected signed challenge document at {}.".format(endpoint))
    match = re.fullmatch(r"([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]{43})", document["challenge"])
    need(match, "Unexpected signed challenge format at {}.".format(endpoint))
    def decode(token):
        return base64.urlsafe_b64decode(token + "=" * (-len(token) % 4))
    payload = json.loads(decode(match[1]))
    need(isinstance(payload, dict) and set(payload) == {"Origin", "KeyId", "Deadline"} and
         payload["Origin"] == origin.lower() and payload["KeyId"] == probe and
         type(payload["Deadline"]) is int and payload["Deadline"] > 0 and len(decode(match[2])) == 32,
         "Signed challenge is not bound to the expected origin and probe key.")


def nginx80():
    master = Path("/run/nginx.pid")
    need(master.is_file() and master.stat().st_uid == 0, "Missing the system nginx PID file.")
    pid = master.read_text().strip()
    need(pid.isdecimal() and Path("/proc", pid, "comm").read_text().strip() == "nginx",
         "Expected the system nginx master.")
    sockets = output("ss", "-H", "-ltnpe", "sport = :80").splitlines()
    # Unprivileged ss may hide the PID. Use the root-owned master and socket UID
    # available to dt2prod; public HTTP is checked separately by verify.
    need(sockets and all(not re.search(r"\buid:[1-9][0-9]*\b", row) and
                         ("users:" not in row or '"nginx"' in row) for row in sockets),
         "Expected a system nginx listener on port 80.")


def disk(required=0):
    used, free, percent, warning, stop = map(
        int, output("bash", BUNDLE / "server-disk-usage.sh", BASE).split())
    needed_kib = (required + 1023) // 1024
    need(percent < stop and free >= needed_kib and
         (used + needed_kib) * 100 < (used + free) * stop,
         "Insufficient free space or projected usage at or above {}%.".format(stop))
    return "{}% used{}".format(percent, " (warning)" if percent >= warning else "")


def owned_directory(path, private=False):
    if not exists(path):
        return
    need(path.is_dir() and path.resolve() == path, "Unsafe directory: {}".format(path))
    info = path.stat()
    need(info.st_uid == os.getuid() and os.access(str(path), os.W_OK | os.X_OK) and
         not (info.st_mode & (0o077 if private else 0o022)),
         "Directory ownership or permissions conflict: {}".format(path))


def inode(path):
    info = path.lstat()
    return [info.st_dev, info.st_ino]


def read_state():
    owned_directory(CONTROL, private=True)
    return read_control(STATE) if exists(STATE) else None


def previous_value(path):
    return os.readlink(str(path)) if path.is_symlink() else None


def managed_paths(config, state):
    for path in (BASE / "config", BASE / "keys", BASE / "store", CONTROL):
        owned_directory(path, private=True)
    for path in (BASE / "publish", BASE / "tools", VERSIONS, LEGACY):
        owned_directory(path)
    need((not exists(CURRENT) and not (state and state["active"])) or
         (state and state["active"] and previous_value(CURRENT) == state["active"]),
         "Unmanaged active publish link.")
    if state:
        need(state.get("store_id") is not None, "Installed authority identity is missing. Review the existing authority.")
        recognized_store(state["store_id"])
        need(read_json(BASE / "config/server.json") == server_config(config),
             "The installed origin, signing identity or server settings differ.")
        for name, mode in (("signing.pem", 0o400), ("release-public.pem", 0o644)):
            path = BASE / "keys" / name
            need(path.is_file() and not path.is_symlink() and path.stat().st_uid == os.getuid() and
                 stat.S_IMODE(path.stat().st_mode) == mode, "Signing key access conflict: {}".format(name))
        need(digest(BASE / "keys/release-public.pem") == state["public_key"],
             "Signing public key differs from the installed identity.")
        generated_public = command("openssl", "pkey", "-in", BASE / "keys/signing.pem", "-passin", "pass:",
                                   "-pubout", "-outform", "DER", stdout=subprocess.PIPE)
        recorded_public = command("openssl", "pkey", "-pubin", "-in", BASE / "keys/release-public.pem",
                                  "-outform", "DER", stdout=subprocess.PIPE)
        need(generated_public.stdout == recorded_public.stdout, "The installed signing key pair no longer matches.")
        need(RUNNER.is_file() and not RUNNER.is_symlink() and digest(RUNNER) == state["runner"],
             "Cron supervisor differs from the installed file.")
        need(GUARD.is_file() and not GUARD.is_symlink() and digest(GUARD) == state["guard"],
             "Startup guard differs from the installed file.")
        if state["active"] and state["boot"] == "systemd":
            need(UNIT.is_file() and not UNIT.is_symlink() and digest(UNIT) == state["unit"],
                 "User unit differs from the installed file.")
    else:
        for relative in ("config/server.json", "config/access-policy.json", "keys/signing.pem",
                         "keys/release-public.pem", "tools/dt-setup-cron.py", "tools/dt_setup_guard.py"):
            need(not exists(BASE / relative), "Unmanaged destination: /dt/{}".format(relative))
        store = BASE / "store"
        need(not exists(store) or not any(store.iterdir()), "An existing authority needs separate review.")
    for name in ("ue-dt-updates", "incoming"):
        original, backup = BASE / name, LEGACY / name
        expected = state and state.get("legacy", {}).get(name)
        if expected and state["active"]:
            need(exists(backup) and not backup.is_symlink() and inode(backup) == expected and
                 (previous_value(original) == str(backup) if name == "ue-dt-updates" else not exists(original)),
                 "Legacy backup or original changed: {}".format(name))
        elif expected:
            need(original.is_dir() and not original.is_symlink() and inode(original) == expected and not exists(backup),
                 "Restored legacy path changed: {}".format(name))
        else:
            need(not exists(backup) and (not exists(original) or
                 (original.is_dir() and not original.is_symlink() and original.stat().st_dev == BASE.stat().st_dev)),
                 "Unsafe legacy path or backup collision: {}".format(name))
    if config["nginxMode"] == "coexist":
        need((BASE / "ue-dt-updates").is_dir(), "Coexist requires the original legacy document root.")


def user_unit(*args):
    return command("systemctl", "--user", *args)


def cron_lines():
    result = subprocess.run(["crontab", "-l"], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            universal_newlines=True)
    if result.returncode == 0:
        return result.stdout
    need(result.returncode == 1 and result.stderr.strip() == "no crontab for dt2prod",
         "Cannot read the dt2prod crontab.")
    return None


def cron_write(contents):
    if contents is None:
        if cron_lines() is not None:
            command("crontab", "-r")
    else:
        command("crontab", "-", input=contents, universal_newlines=True)


def boot_options():
    linger = succeeds("loginctl", "show-user", "dt2prod", "-p", "Linger", "--value") and \
        output("loginctl", "show-user", "dt2prod", "-p", "Linger", "--value") == "yes"
    user_bus = succeeds("systemctl", "--user", "show-environment")
    cron = bool(shutil.which("crontab")) and succeeds("pgrep", "-x", "crond")
    return linger, user_bus, cron


def check(config, state, bundle, recovery=False):
    failures = []

    def row(label, operation):
        try:
            result = operation()
            print("PASS {:<16} {}".format(label, result or ""))
        except (SetupError, OSError, ValueError, subprocess.SubprocessError) as error:
            failures.append(label)
            print("FAIL {:<16} {}".format(label, error))

    def os_check():
        release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines()
                       if "=" in line)
        name, version = (release.get(key, "").strip('"') for key in ("ID", "VERSION_ID"))
        need(name in ("rhel", "rocky") and version == "8.4" and platform.machine() == "x86_64",
             "Expected RHEL/Rocky 8.4 x86_64.")
        return "{} {}".format(name, version)

    def filesystem():
        need(BASE.resolve() == BASE and BASE.is_dir(), "Expected canonical /dt.")
        fs, source, options = output("findmnt", "-nro", "FSTYPE,SOURCE,OPTIONS", "-M", "/dt").split()
        need(fs == "xfs" and source.startswith("/dev/") and
             not {"noexec", "ro"}.intersection(options.split(",")), "Expected writable executable local /dt XFS.")
        mountpoints = output("findmnt", "-nro", "TARGET", "-R", "-M", "/dt").splitlines()
        managed = ("store", "config", "keys", "publish", "tools", "incoming", "ue-dt-updates",
                   ".dt-legacy", ".dt-setup")
        for relative in managed:
            path = "/dt/" + relative
            need(not any(mount == path or mount.startswith(path + "/") for mount in mountpoints),
                 "Nested mount in an installation path: " + path)
        return "{} {}".format(fs, source)

    def tools():
        required = "awk bash curl df findmnt getconf install loginctl openssl pgrep realpath ss systemctl".split()
        missing = [name for name in required if not shutil.which(name)]
        need(not missing, "Missing {}".format(", ".join(missing)))

    def write_access():
        need(os.access(str(BASE), os.W_OK | os.X_OK), "dt2prod needs write/search access on /dt.")
        if not recovery:
            managed_paths(config, state)

    def backend():
        if recovery:
            return "pending transaction, rechecked after recovery"
        listeners = listener(18500)
        if state and state["active"]:
            need(all(row.split()[3] == "127.0.0.1:18500" for row in listeners.splitlines()),
                 "Unexpected port-18500 binding.")
            if listeners:
                need(service_active(state["boot"]),
                     "Port 18500 is occupied by an unverified service.")
        else:
            need(not listeners, "Port 18500 is occupied.")

    def boot():
        linger, user_bus, cron = boot_options()
        mode = (state or {}).get("boot") if state and state["active"] else None
        if mode == "systemd" or (mode is None and linger and user_bus):
            need(linger and user_bus, "The installed user service needs linger and a working user bus.")
            return "linger=yes, user bus=ready, crond={}".format(cron)
        need(cron, "Enable linger and the user bus or provide an active crond.")
        cron_lines()
        return "linger={}, user bus={}, crond=ready (@reboot)".format(linger, user_bus)

    row("Tools", tools)
    row("OS", os_check)
    row("glibc", lambda: need(output("getconf", "GNU_LIBC_VERSION") == "glibc 2.28", "Expected glibc 2.28."))
    row("Filesystem", filesystem)
    if recovery:
        row("Disk", lambda: "new publish capacity checked after recovery")
    else:
        saved = VERSIONS / ("release-" + bundle[0])
        needed = 0 if exists(saved) else sum((BUNDLE / name).stat().st_size for name in bundle[1]
                                            if name.startswith(("linux-x64/", "server-linux-x64/")))
        row("Disk", lambda: disk(needed))
    row("/dt access", write_access)
    row("nginx :80", nginx80)
    row("Backend :18500", backend)
    row("Boot", boot)
    row("Bundle", lambda: bundle[0])
    return not failures


def service_active(mode):
    if mode == "systemd":
        return succeeds("systemctl", "--user", "is-active", "--quiet", UNIT_NAME)
    return succeeds(sys.executable, "-B", str(RUNNER), "status")


def start(mode):
    if mode == "systemd":
        user_unit("start", UNIT_NAME)
    else:
        command(sys.executable, "-B", RUNNER, "start")
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if service_active(mode):
            try:
                if curl_code(LOOPBACK + CATALOG) == "401":
                    return
            except subprocess.CalledProcessError:
                pass
        time.sleep(0.25)
    raise SetupError("Service startup requires an owned listener and loopback Catalog HTTP 401.")


def stop(mode):
    if mode == "systemd":
        user_unit("stop", UNIT_NAME)
    else:
        command(sys.executable, "-B", RUNNER, "stop")
    need(not listener(18500), "Port 18500 did not become free after stopping the service.")


def reload_unit():
    user_unit("daemon-reload")


def configure_boot(tx, mode, enable):
    if mode == "systemd":
        tx.record("systemd", enable=not enable)
        if enable:
            tx.directory(UNIT.parent)
            tx.file(UNIT, (BUNDLE / UNIT_NAME).read_text(encoding="utf-8"))
            reload_unit()
            user_unit("enable", UNIT_NAME)
        else:
            user_unit("disable", UNIT_NAME)
            tx.file(UNIT, None)
            reload_unit()
    else:
        old = cron_lines()
        lines = (old or "").splitlines(keepends=True)
        need(sum(line.rstrip("\n") == CRON_JOB for line in lines) == int(not enable),
             "Managed cron entry changed.")
        new = ((old or "") + CRON_JOB + "\n" if enable else
               "".join(line for line in lines if line.rstrip("\n") != CRON_JOB))
        tx.record("cron", old=old, new=new or None)
        cron_write(new or None)


def file_content(path):
    if not exists(path):
        return None
    need(path.is_file() and not path.is_symlink(), "Expected a regular control file: {}".format(path))
    return path.read_text(encoding="utf-8")


def set_file(path, contents):
    if contents is None:
        if path.exists():
            path.unlink()
            sync_dir(path.parent)
    else:
        atomic_write(path, contents)


def set_link(path, target, temporary):
    if exists(temporary):
        need(temporary.is_symlink() and previous_value(temporary) == target,
             "Temporary link changed: {}".format(temporary))
    if target is None:
        if path.is_symlink():
            path.unlink()
    else:
        if not exists(temporary):
            temporary.symlink_to(target)
        os.replace(str(temporary), str(path))
    sync_dir(path.parent)


def rename(source, dest):
    need(not exists(dest), "Destination already exists: {}".format(dest))
    os.rename(str(source), str(dest))
    sync_dir(source.parent)
    if source.parent != dest.parent:
        sync_dir(dest.parent)


class Transaction:
    def __init__(self, operation, created):
        self.data = {"id": uuid.uuid4().hex, "operation": operation, "undo": [],
                     "mode": None, "restart": False, "created_control": created}
        self.stage = CONTROL / self.data["id"]
        self.stage.mkdir(mode=0o700)
        self.data["stage_id"] = identity(self.stage, stat.S_ISDIR)
        write_json(self.stage / ".owner.json", {"transaction": self.data["id"], "stage": self.data["stage_id"]})
        self.save()

    def save(self):
        write_json(PENDING, self.data)

    def record(self, kind, **action):
        action["kind"] = kind
        self.data["undo"].append(action)
        self.save()
        return action

    def directory(self, path, mode=0o700):
        if not exists(path):
            self.directory(path.parent)
            action = self.record("directory", path=str(path), after=None)
            path.mkdir(mode=mode)
            # umask 077 would prevent nginx from traversing the legacy backup.
            path.chmod(mode)
            action["after"] = identity(path, stat.S_ISDIR)
            self.save()
            sync_dir(path.parent)

    def move(self, source, dest):
        self.record("rename", src=str(source), dest=str(dest), inode=inode(source),
                    uid=source.lstat().st_uid)
        rename(source, dest)

    def file(self, path, contents):
        action = self.record("file", path=str(path), old=file_content(path), new=contents,
                             before=control_identity(path, stat.S_ISREG), after=None)
        set_file(path, contents)
        action["after"] = control_identity(path, stat.S_ISREG)
        self.save()

    def link(self, path, target):
        need(not exists(path) or path.is_symlink(), "Link target is occupied: {}".format(path))
        temporary = path.parent / (".dt-setup-link-" + self.data["id"])
        need(not exists(temporary), "Unexpected temporary link: {}".format(temporary))
        action = self.record("link", path=str(path), old=previous_value(path), new=target,
                             before=control_identity(path, stat.S_ISLNK), after=None)
        set_link(path, target, temporary)
        action["after"] = control_identity(path, stat.S_ISLNK)
        self.save()

    def service(self, mode, restart):
        self.data["mode"] = mode
        self.data["restart"] = restart
        self.save()

    def commit(self, state):
        validate_journal(self.data)
        state["last_tx"] = self.data["id"]
        write_json(STATE, state)
        finish_transaction(self.data)

    def start_service(self, mode, target, store_id):
        start_pending(self.data, mode, target, store_id)


def control_identity(path, kind):
    return identity(path, kind) if exists(path) else None


def inode_record(value):
    return isinstance(value, list) and len(value) == 2 and all(type(n) is int and n >= 0 for n in value)


def validate_journal(data):
    need(isinstance(data, dict) and isinstance(data.get("id"), str) and UUID.fullmatch(data["id"]) and
         inode_record(data.get("stage_id")) and isinstance(data.get("undo"), list) and
         data.get("operation") in ("install", "update", "rollback") and
         data.get("mode") in (None, "cron", "systemd") and type(data.get("restart")) is bool and
         type(data.get("created_control")) is bool,
         "Invalid transaction journal; preserve it for review.")
    operation = data["operation"]
    need(not data["created_control"] or operation == "install", "Invalid journal control creation.")
    stage = CONTROL / data["id"]
    if exists(stage):
        need(stage.resolve() == stage and identity(stage, stat.S_ISDIR) == data["stage_id"],
             "Transaction staging directory identity changed.")
        need(read_control(stage / ".owner.json") ==
             {"transaction": data["id"], "stage": data["stage_id"]},
             "Transaction staging marker changed.")
    else:
        need(data.get("cleaning") is True, "Transaction staging directory is missing.")

    legacy_pairs = {(str(BASE / name), str(LEGACY / name)) for name in ("incoming", "ue-dt-updates")}
    install_pairs = {(str(stage / "store"), str(BASE / "store")),
                     (str(BASE / "store"), str(stage / "empty-store"))} | legacy_pairs
    for folder, names in (("config", ("server.json", "access-policy.json")),
                          ("keys", ("signing.pem", "release-public.pem"))):
        install_pairs.update((str(stage / name), str(BASE / folder / name)) for name in names)
    rollback_pairs = {(dest, src) for src, dest in legacy_pairs}
    directories = {str(BASE / folder) for folder in ("config", "keys", "publish", "tools")}
    directories.update(str(p) for p in (LEGACY, VERSIONS, UNIT.parent, UNIT.parent.parent))
    for action in data["undo"]:
        need(isinstance(action, dict), "Invalid journal action.")
        kind = action.get("kind")
        path = action.get("path")
        if kind == "rename":
            source, dest = action.get("src"), action.get("dest")
            need(isinstance(source, str) and isinstance(dest, str), "Invalid journal rename paths.")
            publish = source == str(stage / "publish") and version_path(dest)
            allowed = (operation == "install" and ((source, dest) in install_pairs or publish) or
                       operation == "update" and publish or
                       operation == "rollback" and (source, dest) in rollback_pairs)
            need(allowed and inode_record(action.get("inode")) and type(action.get("uid")) is int,
                 "Journal rename is outside the operation's permitted paths.")
        elif kind == "directory":
            need(operation == "install" and isinstance(path, str) and path in directories and
                 (action.get("after") is None or inode_record(action["after"])),
                 "Journal directory is outside the operation's permitted paths.")
        elif kind in ("file", "link"):
            old, new = action.get("old"), action.get("new")
            if kind == "file":
                allowed = ((path in (str(RUNNER), str(GUARD)) and operation == "install" and old is None) or
                           (path == str(UNIT) and data["mode"] == "systemd" and
                            (operation == "install" and old is None or operation == "rollback" and new is None)))
                allowed = allowed and (old is None) != (new is None) and isinstance(old or new, str)
            else:
                need(all(value is None or isinstance(value, str) for value in (old, new)),
                     "Invalid journal link value.")
                allowed = (path == str(CURRENT) and all(value is None or version_path(value)
                                                       for value in (old, new)) or
                           path == str(BASE / "ue-dt-updates") and operation in ("install", "rollback") and
                           {old, new} == {None, str(LEGACY / "ue-dt-updates")})
            need(allowed and all(action.get(key) is None or inode_record(action[key])
                                 for key in ("before", "after")), "Unrecognized journal {} target.".format(kind))
        elif kind == "cron":
            old, new = action.get("old"), action.get("new")
            need(data["mode"] == "cron" and operation in ("install", "rollback") and
                 all(value is None or isinstance(value, str) for value in (old, new)),
                 "Invalid journal cron action.")
            without, with_job = (old, new) if operation == "install" else (new, old)
            lines = (with_job or "").splitlines(keepends=True)
            need(lines.count(CRON_JOB + "\n") == 1 and
                 "".join(line for line in lines if line != CRON_JOB + "\n") == (without or ""),
                 "Journal cron action would change unrelated entries.")
        elif kind == "systemd":
            need(data["mode"] == "systemd" and operation in ("install", "rollback") and
                 action.get("enable") is (operation == "rollback"), "Invalid journal systemd action.")
        else:
            raise SetupError("Unrecognized journal action.")
        for key in ("path", "src", "dest"):
            if key in action:
                item = Path(action[key])
                need(item.parent.resolve() == item.parent, "Linked journal parent: {}".format(item))
    return stage


def unchanged_rename(path, action):
    if not exists(path):
        return False
    info = path.lstat()
    return (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)) and \
        [info.st_dev, info.st_ino] == action["inode"] and info.st_uid == action["uid"]


def validate_undo_identities(data, state):
    # Validate entries visible now before stopping a live service. Reverse replay
    # verifies the intermediate identities as newer actions uncover them.
    affected = set()
    for action in reversed(data["undo"]):
        kind = action["kind"]
        if kind == "rename":
            source, dest = Path(action["src"]), Path(action["dest"])
            if not affected.intersection((source, dest)):
                need(unchanged_rename(source, action) or unchanged_rename(dest, action),
                     "Moved entry identity changed: {}".format(dest))
            affected.update((source, dest))
        elif kind in ("link", "file", "directory"):
            path = Path(action["path"])
            if kind == "file" and path == UNIT and action["old"] is not None:
                need(state and hashlib.sha256(action["old"].encode("utf-8")).hexdigest() == state["unit"],
                     "Former user unit identity changed in the journal.")
            if path not in affected:
                if kind == "directory":
                    need(not exists(path) or identity(path, stat.S_ISDIR) == action["after"],
                         "Created directory identity changed: {}".format(path))
                else:
                    file_type, read_value = (stat.S_ISLNK, previous_value) if kind == "link" else \
                        (stat.S_ISREG, file_content)
                    current = (control_identity(path, file_type), read_value(path))
                    need(current in ((action["before"], action["old"]), (action["after"], action["new"])),
                         "Managed control identity changed: {}".format(path))
            affected.add(path)
    mode = data["mode"]
    if mode == "cron" and exists(RUNNER):
        expected = state["runner"] if state else digest(BUNDLE / "dt-setup-cron.py")
        need(RUNNER.is_file() and RUNNER.resolve() == RUNNER and digest(RUNNER) == expected,
             "Recovery supervisor identity changed.")
    if mode == "systemd" and exists(UNIT):
        expected = state.get("unit") if state and state["active"] else digest(BUNDLE / UNIT_NAME)
        need(UNIT.is_file() and UNIT.resolve() == UNIT and digest(UNIT) == expected,
             "Recovery user unit identity changed.")


def finish_transaction(data):
    stage = validate_journal(data)
    data["cleaning"] = True
    write_json(PENDING, data)
    if exists(stage):
        allowed = {".owner.json", "publish", "store", "empty-store", "server.json",
                   "access-policy.json", "signing.pem", "release-public.pem"}
        need(all(entry.name in allowed for entry in stage.iterdir()), "Unexpected files in transaction staging.")
        for parent, dirs, files in os.walk(str(stage), followlinks=False):
            for name in dirs + files:
                item = Path(parent) / name
                info = item.lstat()
                need(info.st_uid == os.getuid() and info.st_dev == data["stage_id"][0] and
                     (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode)),
                     "Unexpected ownership, device or type in staging: {}".format(item))
        shutil.rmtree(str(stage))
    PENDING.unlink()
    sync_dir(CONTROL)


def start_pending(data, mode, target, store_id):
    data["start"] = {"owner": process_stamp(os.getpid()), "active": target, "store_id": store_id}
    write_json(PENDING, data)
    try:
        start(mode)
    finally:
        data.pop("start")
        write_json(PENDING, data)


def undo_action(action, transaction_id):
    kind = action["kind"]
    if kind == "rename":
        source, dest = Path(action["src"]), Path(action["dest"])
        if unchanged_rename(dest, action):
            rename(dest, source)
        else:
            need(unchanged_rename(source, action) and not exists(dest),
                 "Moved entry changed: {}".format(dest))
    elif kind == "link":
        path = Path(action["path"])
        temp = path.parent / (".dt-setup-link-" + transaction_id)
        need(not exists(temp), "Unrecognized temporary link: {}".format(temp))
        current = (control_identity(path, stat.S_ISLNK), previous_value(path))
        before = (action["before"], action["old"])
        if current != before:
            need(current == (action["after"], action["new"]), "Active or legacy link changed: {}".format(path))
            set_link(path, action["old"], temp)
    elif kind == "directory":
        path = Path(action["path"])
        if exists(path):
            need(identity(path, stat.S_ISDIR) == action["after"], "Created directory identity changed: {}".format(path))
            path.rmdir()
            sync_dir(path.parent)
    elif kind == "file":
        path = Path(action["path"])
        current = (control_identity(path, stat.S_ISREG), file_content(path))
        if current != (action["before"], action["old"]):
            need(current == (action["after"], action["new"]), "Control file changed: {}".format(path))
            set_file(path, action["old"])
    elif kind == "cron":
        need(cron_lines() in (action["old"], action["new"]), "Crontab changed during the operation.")
        if cron_lines() != action["old"]:
            cron_write(action["old"])
    elif kind == "systemd":
        reload_unit()
        if action["enable"]:
            user_unit("enable", UNIT_NAME)


def recover_pending():
    if not exists(PENDING):
        return
    pending = read_control(PENDING)
    validate_journal(pending)
    state = read_state()
    if state and state.get("last_tx") == pending["id"]:
        finish_transaction(pending)
        return
    need(not pending.get("cleaning") or not pending["undo"],
         "Uncommitted journal entered cleanup before undo completed.")
    validate_undo_identities(pending, state)
    print("Restoring interrupted or failed {}.".format(pending["operation"]), flush=True)
    mode = pending["mode"]
    if mode and not pending.get("undo_started"):
        if (mode == "cron" and RUNNER.exists()) or service_active(mode):
            stop(mode)
        if mode == "systemd" and any(a["kind"] == "systemd" and not a["enable"] for a in pending["undo"]):
            if UNIT.exists():
                user_unit("disable", UNIT_NAME)
        pending["undo_started"] = True
        write_json(PENDING, pending)
    while pending["undo"]:
        undo_action(pending["undo"][-1], pending["id"])
        pending["undo"].pop()
        write_json(PENDING, pending)
    if pending["restart"]:
        need(state and state.get("store_id"), "Missing installed authority identity for recovery.")
        start_pending(pending, mode, state["active"], state["store_id"])
    if pending["created_control"] and not exists(STATE):
        for name in ("cron.log", "cron.lock", "cron.sock"):
            path = CONTROL / name
            if exists(path):
                identity(path, stat.S_ISSOCK if name == "cron.sock" else stat.S_ISREG)
                path.unlink()
    finish_transaction(pending)
    if pending["created_control"] and not exists(STATE):
        CONTROL.rmdir()
        sync_dir(BASE)


@contextlib.contextmanager
def mutation(operation):
    created = not CONTROL.exists()
    if created:
        CONTROL.mkdir(mode=0o700)
        sync_dir(BASE)
    try:
        yield Transaction(operation, created)
    except BaseException:
        recover_pending()
        if created and CONTROL.exists() and not exists(STATE):
            CONTROL.rmdir()
            sync_dir(BASE)
        raise


def stage_publish(tx, bundle):
    release = "release-" + bundle[0]
    target = VERSIONS / release
    expected = {name: sha for name, sha in bundle[1].items()
                if name.startswith(("linux-x64/", "server-linux-x64/"))}
    if not exists(target):
        stage = tx.stage / "publish"
        stage.mkdir(mode=0o700)
        for folder in ("linux-x64", "server-linux-x64"):
            shutil.copytree(str(BUNDLE / folder), str(stage / folder))
        for path in payload_files(stage):
            relative = str(path.relative_to(stage))
            need(bundle[1].get(relative) == digest(path), "Payload changed while staging: " + relative)
        write_json(stage / "payload.json", expected)
        # Flush the staged publish before making it selectable after a reboot.
        for parent, _, files in os.walk(str(stage), topdown=False):
            for name in files:
                with open(Path(parent) / name, "rb") as stream:
                    os.fsync(stream.fileno())
            sync_dir(Path(parent))
        tx.move(stage, target)
    check_version(str(target))
    need(read_json(target / "payload.json") == expected, "Saved publish belongs to a different bundle.")
    return str(target)


def check_version(target):
    path = Path(target)
    need(path.parent == VERSIONS and re.fullmatch(r"release-[a-f0-9]{64}", path.name), "Invalid publish reference.")
    owned_directory(path, private=True)
    found = {str(p.relative_to(path)): digest(p) for p in payload_files(path)
             if p != path / "payload.json"}
    need(found == read_json(path / "payload.json") and (path / SERVER).is_file() and
         os.access(str(path / SERVER), os.X_OK) and os.access(str(path / LAUNCHER), os.X_OK),
         "Saved publish does not match its recorded inventory.")


def prepare_authority(tx, config, candidate, state):
    if state:
        return state["public_key"]
    print("Preparing signed HTTP configuration, signing identity and empty authority.", flush=True)
    stage = tx.stage
    command("bash", BUNDLE / "prepare-server-store.sh", "--user", stage / "store")
    command(Path(candidate) / LAUNCHER, "generate-signing-key", "--private-key",
            stage / "signing.pem", "--public-key", stage / "release-public.pem")
    (stage / "signing.pem").chmod(0o400)
    (stage / "release-public.pem").chmod(0o644)
    write_json(stage / "server.json", server_config(config))
    write_json(stage / "access-policy.json", {"clients": []})
    tx.directory(BASE / "config")
    tx.directory(BASE / "keys")
    if exists(BASE / "store"):
        need(not any((BASE / "store").iterdir()), "The existing store is no longer empty.")
        tx.move(BASE / "store", stage / "empty-store")
    tx.move(stage / "store", BASE / "store")
    for folder, names in (("config", ("server.json", "access-policy.json")),
                          ("keys", ("signing.pem", "release-public.pem"))):
        for name in names:
            tx.move(stage / name, BASE / folder / name)
    return digest(BASE / "keys/release-public.pem")


def install(config, state, bundle):
    if state and state["active"]:
        need(state["active"] == str(VERSIONS / ("release-" + bundle[0])),
             "Another bundle is installed. Use update.")
        check_version(state["active"])
        start(state["boot"])
        print("The same bundle is installed and responding.")
        return
    linger, user_bus, _ = boot_options()
    mode = "systemd" if linger and user_bus else "cron"
    if mode == "systemd":
        need(not exists(UNIT) and not succeeds("systemctl", "--user", "cat", UNIT_NAME),
             "Existing user unit conflicts with the install.")
        need(UNIT.parent.resolve() == UNIT.parent, "User unit path must not contain symlinks.")
    else:
        need("uedt-dt-setup" not in (cron_lines() or ""), "Existing managed cron entry conflicts.")
    with mutation("install") as tx:
        print("Staging verified server and CLI.", flush=True)
        tx.directory(BASE / "publish")
        tx.directory(VERSIONS)
        tx.directory(BASE / "tools")
        candidate = stage_publish(tx, bundle)
        public_key = prepare_authority(tx, config, candidate, state)
        if not state:
            tx.file(RUNNER, (BUNDLE / "dt-setup-cron.py").read_text(encoding="utf-8"))
            tx.file(GUARD, (BUNDLE / GUARD.name).read_text(encoding="utf-8"))
            # Bootstrap is limited to the store created by this install.
            command(Path(candidate) / SERVER, "list", "--config", BASE / "config/server.json",
                    stdout=subprocess.DEVNULL)
        store_id = recognized_store(state["store_id"] if state else None)
        legacy = {}
        print("Backing up legacy paths without deleting their contents.", flush=True)
        tx.directory(LEGACY, 0o711)
        for name in ("ue-dt-updates", "incoming"):
            source, dest = BASE / name, LEGACY / name
            if source.exists():
                legacy[name] = inode(source)
                tx.move(source, dest)
                if name == "ue-dt-updates":
                    # Keep the existing nginx configuration and the root-side rollback usable.
                    tx.link(source, str(dest))
        if config["nginxMode"] == "coexist":
            need(curl_code(config["publicUrl"] + config["legacyProbe"]) == "200",
                 "Existing nginx could not read the relocated legacy probe.")
        tx.link(CURRENT, candidate)
        tx.service(mode, False)
        configure_boot(tx, mode, True)
        print("Starting {} service and checking loopback Catalog.".format(mode), flush=True)
        tx.start_service(mode, candidate, store_id)
        tx.commit({"active": candidate, "history": [], "boot": mode,
                   "legacy": legacy, "public_key": public_key, "runner": digest(RUNNER),
                   "store_id": store_id, "guard": digest(GUARD),
                   "unit": digest(UNIT) if mode == "systemd" else None})
        print("Install complete. Generate the one-time administrator request.")


def update(state, bundle):
    need(state and state["active"], "Install before updating.")
    if state["active"] == str(VERSIONS / ("release-" + bundle[0])):
        check_version(state["active"])
        start(state["boot"])
        print("The same bundle is active and responding.")
        return
    check_version(state["active"])
    need(service_active(state["boot"]) and curl_code(LOOPBACK + CATALOG) == "401",
         "Verify the current service before updating.")
    with mutation("update") as tx:
        print("Staging verified server and CLI.", flush=True)
        candidate = stage_publish(tx, bundle)
        tx.service(state["boot"], True)
        print("Stopping the service and selecting the new publish.", flush=True)
        stop(state["boot"])
        tx.link(CURRENT, candidate)
        tx.start_service(state["boot"], candidate, state["store_id"])
        tx.commit(dict(state, active=candidate, history=state["history"] + [state["active"]]))
        print("Update complete.")


def rollback(state):
    need(state and state["active"], "There is no active install or update to roll back.")
    mode, history = state["boot"], state["history"]
    previous = history[-1] if history else None
    if previous:
        check_version(previous)
    with mutation("rollback") as tx:
        tx.service(mode, service_active(mode))
        stop(mode)
        tx.link(CURRENT, previous)
        if previous:
            tx.start_service(mode, previous, state["store_id"])
        else:
            configure_boot(tx, mode, False)
            for name in reversed(list(state["legacy"])):
                if name == "ue-dt-updates":
                    tx.link(BASE / name, None)
                tx.move(LEGACY / name, BASE / name)
        tx.commit(dict(state, active=previous, history=history[:-1]))
        print("Rollback complete. Authority, access policy and signing keys are preserved.")


def verify(config, state):
    need(state and state["active"], "No installed service. Run install.")
    need(service_active(state["boot"]), "The installed service is inactive.")
    nginx80()
    for name, url, expected in (
        ("Loopback Catalog", LOOPBACK + CATALOG, "401"),
        ("Public Catalog", config["publicUrl"] + CATALOG, "401"),
        ("Legacy probe", config["publicUrl"] + config["legacyProbe"],
         "200" if config["nginxMode"] == "coexist" else "404"),
    ):
        actual = curl_code(url)
        need(actual == expected, "{} returned {}; expected {}.".format(name, actual, expected))
        print("PASS {} HTTP {}".format(name, actual))
    for endpoint in (LOOPBACK, config["publicUrl"]):
        signed_challenge(endpoint, config["publicUrl"])
        print("PASS signed challenge at {}".format(endpoint))
    print("PASS {} service and disk {}".format(state["boot"], disk()))


def admin_request(config, state, bundle, directory):
    need(state and state["active"] and service_active(state["boot"]) and
         curl_code(LOOPBACK + CATALOG) == "401", "Start the installed backend and verify loopback first.")
    mode = config["nginxMode"]
    candidate = "nginx/{}/ue-dt-updates.conf".format(mode)
    snippet = "nginx/uedt-signed-proxy.inc"
    root_commands = r"""set -eu
site=/etc/nginx/conf.d/ue-dt-updates.conf; test -f "$site" && test ! -L "$site" && test -f /root/ue-dt-updates.candidate && test ! -L /root/ue-dt-updates.candidate || exit 1; backup=$(mktemp /etc/nginx/ue-dt-updates.before.XXXXXXXX); cp -a -- "$site" "$backup"; printf 'backup=%s\n' "$backup"
candidate=$(mktemp /etc/nginx/conf.d/.ue-dt-updates.XXXXXXXX); install -o root -g root -m 0644 /root/ue-dt-updates.candidate "$candidate"; mv -T -- "$candidate" "$site"
if ! (nginx -t && systemctl reload nginx); then restore=$(mktemp /etc/nginx/conf.d/.ue-dt-updates.restore.XXXXXXXX); cp -a -- "$backup" "$restore"; mv -T -- "$restore" "$site"; nginx -t && systemctl reload nginx; exit 1; fi
printf 'Active site: %s\n' "$site"
"""
    restore_command = r"""site=/etc/nginx/conf.d/ue-dt-updates.conf
backup=/etc/nginx/ue-dt-updates.before.REPLACE_WITH_PRINTED_SUFFIX
restore=$(mktemp /etc/nginx/conf.d/.ue-dt-updates.restore.XXXXXXXX) &&
    cp -a -- "$backup" "$restore" && mv -T -- "$restore" "$site" &&
    nginx -t && systemctl reload nginx
"""
    lines = [
        "Keep the existing nginx port-80 default vhost and preserve its original file.",
        "Proxy signed Catalog, challenge and release requests to 127.0.0.1:18500.",
        "Use {} for legacy static clients at {}.".format(mode, config["publicUrl"]),
        "",
        "Reviewed bundle candidates (check against an independently trusted bundle SHA256):",
    ]
    paths = ("nginx/coexist/ue-dt-updates.conf", "nginx/cutover/ue-dt-updates.conf", snippet)
    lines.extend("{}  {}".format(bundle[1][path], path) for path in paths)
    lines.append("Selected: " + candidate)
    lines.extend([
        "",
        "Stage the verified files under root control. Expand the snippet into all three locations",
        "in /root/ue-dt-updates.candidate. For coexist, merge the signed locations and log format",
        "into the actual original vhost if its legacy rules differ. Compare with nginx -T.",
        "Check inherited authentication, rewrites, cache, errors, logging and root traversal.",
        "",
        "Prepare the candidate (root shell, after extracting the verified bundle under /root):",
        "set -eu",
        "bundle=/root/ue-dt-server-rhel84; mode=" + mode,
        'candidate=$(mktemp /root/.ue-dt-updates.XXXXXXXX)',
        r"""awk 'FNR==NR { proxy[++n]=$0; next } /^[[:space:]]*include \/etc\/nginx\/uedt-signed-proxy\.inc;$/ { for (i=1;i<=n;i++) print "        " proxy[i]; ++hits; next } { print } END { if (hits!=3) exit 1 }' "$bundle/nginx/uedt-signed-proxy.inc" "$bundle/nginx/$mode/ue-dt-updates.conf" > "$candidate" """.rstrip(),
        'mv -T -- "$candidate" /root/ue-dt-updates.candidate',
        "",
        "Confirm loopback Catalog HTTP 401 before proceeding. Keep the current root session open.",
        "",
        "Apply (five lines in a root shell):", root_commands,
    ])
    if not boot_options()[0]:
        lines.extend(["Only if the handover requires an enabled systemd user manager at boot:",
                      "loginctl enable-linger dt2prod",
                      "This install uses its existing crontab fallback until reinstalled.", ""])
    lines.extend([
        "After reload (expect HTTP 401):",
        """curl --noproxy '*' -sS --max-time 5 -o /dev/null -w '%{{http_code}}\\n' {}""".format(
            shlex.quote(config["publicUrl"] + CATALOG)),
        "",
        "Rollback (substitute the printed backup path):", restore_command,
    ])
    path = directory / "admin-request.txt"
    atomic_write(path, "\n".join(lines))
    print(path)


def interrupted(signum, frame):
    raise SetupError("Interrupted by signal {}.".format(signum))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("check", "install", "admin-request", "verify",
                                              "update", "rollback"))
    parser.add_argument("--config", type=Path, default=BUNDLE / "dt-setup.conf")
    args = parser.parse_args()
    os.umask(0o077)
    os.environ["LC_ALL"] = "C"
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        need(os.getuid() != 0 and output("id", "-un") == "dt2prod", "Run as dt2prod.")
        config = config_for(args.config.resolve(strict=True))
        state = read_state()
        if args.operation in ("verify", "admin-request"):
            need(not exists(PENDING), "Recover the pending transaction with install.")
            managed_paths(config, state)
            if args.operation == "verify":
                verify(config, state)
            else:
                admin_request(config, state, bundle_info(), args.config.resolve().parent)
            return 0
        with contextlib.ExitStack() as stack:
            if args.operation != "check":
                fd = os.open(str(BASE), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                stack.callback(os.close, fd)
                try:
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    raise SetupError("Another dt-setup operation is active.")
                state = read_state()
            pending = exists(PENDING)
            if args.operation == "check":
                need(not pending, "Pending transaction. Run install to recover it.")
                return 0 if check(config, state, bundle_info()) else 1
            if pending:
                need(args.operation == "install", "Run install to recover the pending transaction.")
                need(check(config, state, bundle_info(), recovery=True), "Preflight failed.")
                recover_pending()
                state = read_state()
            if args.operation in ("install", "update"):
                bundle = bundle_info()
                need(check(config, state, bundle), "Preflight failed.")
                if args.operation == "install":
                    install(config, state, bundle)
                else:
                    update(state, bundle)
            else:
                managed_paths(config, state)
                rollback(state)
        return 0
    except (SetupError, ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print("FAIL {}".format(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
