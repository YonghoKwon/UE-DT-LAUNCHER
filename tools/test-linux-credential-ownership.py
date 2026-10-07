#!/usr/bin/env python3
"""Root-only local validation using EXISTING uedt/nobody accounts. No account/service changes.
Creates a uniquely named managed test key; removes only that key afterwards.
"""
import argparse
import json
import os
from pathlib import Path
import pwd
import subprocess
import tempfile
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True)
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise RuntimeError("Run as root in the isolated test environment")
    service = pwd.getpwnam("uedt")
    pwd.getpwnam("nobody")
    root = Path("/etc/ue-dt-launcher/credentials")
    directory_existed = root.exists()
    if not root.parent.is_dir():
        raise RuntimeError("Existing launcher configuration parent is required")
    name = "codex-validation-" + uuid.uuid4().hex
    path = root / (name + ".keycred")
    launcher = str(Path(args.launcher).resolve())
    env = dict(os.environ)
    env.pop("UE_DT_AGENT_DATA_ROOT", None)

    caches = {}
    def run(arguments, expected=0):
        child_env = dict(env)
        if arguments[0] == "runuser":
            child_env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = caches[arguments[2]]
        result = subprocess.run(arguments, env=child_env, capture_output=True, text=True, timeout=30)
        if result.returncode != expected:
            raise RuntimeError("Credential ownership probe failed: " + result.stderr)
        return result.stdout

    try:
        with tempfile.TemporaryDirectory(prefix="uedt-owner-") as temp:
            os.chmod(temp, 0o755)
            for identity in ("uedt", "nobody"):
                user = pwd.getpwnam(identity)
                cache = Path(temp) / identity
                cache.mkdir(mode=0o700)
                os.chown(cache, user.pw_uid, user.pw_gid)
                caches[identity] = str(cache)
            run([launcher, "credential", "keygen", "--name", name, "--key-id", name, "--public-out", str(Path(temp) / "public.json")])
            stat = path.stat()
            assert stat.st_uid == service.pw_uid and stat.st_gid == service.pw_gid and stat.st_mode & 0o777 == 0o600
            allowed = json.loads(run(["runuser", "-u", "uedt", "--", launcher, "credential", "status", "--name", name]))
            denied = json.loads(run(["runuser", "-u", "nobody", "--", launcher, "credential", "status", "--name", name], expected=1))
            assert allowed["ready"] is True and denied["ready"] is False
            print(json.dumps({"linux_service_identity_read": True, "unrelated_identity_denied": True,
                              "owner": "uedt:uedt", "mode": "0600", "account_or_service_created": False}))
    finally:
        if path.exists():
            run([launcher, "credential", "delete", "--name", name])
        if not directory_existed and root.exists():
            root.rmdir()  # fails safely if another file appeared; never recursively remove managed storage


if __name__ == "__main__":
    main()
