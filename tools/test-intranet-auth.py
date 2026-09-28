#!/usr/bin/env python3
"""Isolated synthetic E2E. Uses published binaries, never a company server.

Outputs contain test keys: keep the root outside source control. Only summary.json
is suitable for redacted evidence. Windows payload is a local copy of cmd.exe;
Linux payload is a harmless marker script, not an Unreal package.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--root")
    parser.add_argument("--hold", action="store_true")
    args = parser.parse_args()
    launcher, server = str(Path(args.launcher).resolve()), str(Path(args.server).resolve())
    root = Path(args.root).resolve() if args.root else Path(tempfile.mkdtemp(prefix="uedt-intranet-"))
    root.mkdir(parents=True, exist_ok=True)
    if (root / "server.json").exists():
        raise RuntimeError("Use a new isolated root; existing fixture will not be replaced")
    client = root / "client"
    (client / "agent").mkdir(parents=True)
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(client / "agent"))
    counter = 0
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0

    def run(binary, *arguments, expected=0):
        nonlocal counter
        counter += 1
        result = subprocess.run([binary, *map(str, arguments)], env=env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=120, creationflags=flags)
        (root / f"command-{counter:02d}.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        if result.returncode != expected:
            raise RuntimeError(f"Command {counter} failed ({result.returncode}); inspect isolated log")
        return result.stdout

    def write(path, value):
        path.write_text(json.dumps(value, indent=2), encoding="utf-8")

    run(launcher, "generate-signing-key", "--private-key", root / "release.pem", "--public-key", root / "public.pem")
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    origin = f"http://127.0.0.1:{port}"
    write(root / "server.json", {"root": str(root / "server"), "publicUrl": origin, "listenUrl": origin,
          "signingKeyPath": str(root / "release.pem"), "policyPath": str(root / "policy.json"),
          "authenticationMode": "request-signature-v1"})
    write(root / "policy.json", {"clients": [{"id": "pc-test", "addresses": ["127.0.0.0/8"],
          "grants": [{"projectId": "demo", "environment": "prod", "channel": "stable", "versions": []}]}]})
    run(launcher, "credential", "keygen", "--name", "device", "--key-id", "pc-test-key", "--public-out", root / "device-public.json")
    run(server, "client-key", "add", "--client", "pc-test", "--public-key", root / "device-public.json", "--config", root / "server.json")
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    entry = "game.exe" if os.name == "nt" else "game.sh"
    png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6nD8AAAAASUVORK5CYII=")
    for version in ("1.0.0", "2.0.0"):
        upload = root / "server" / "incoming" / version
        upload.mkdir(parents=True)
        archive = upload / "Package.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as package:
            if os.name == "nt":
                package.write(Path(os.environ["SystemRoot"]) / "System32" / "cmd.exe", entry)
            else:
                package.writestr(entry, '#!/bin/sh\nprintf "UE_DT_FAKE_GAME_OK\\n"\n')
            package.writestr("version.txt", version)
            package.writestr("hero.png", png)
        run(launcher, "release-metadata", "--zip", archive, "--project-id", "demo", "--version", version,
            "--platform", platform, "--entry-point", entry, "--executable-paths", entry,
            "--hero-path", "hero.png", "--output", upload / "release.json")
        job = json.loads(run(server, "ingest", upload, "--config", root / "server.json"))
        if job["state"] != "pending":
            raise RuntimeError(f"Expected pending approval, got {job['state']}")
        run(server, "approve", job["id"], "--config", root / "server.json")
    config = {"schemaVersion": 3, "deploymentMode": "portable", "distributionServerUrl": origin,
              "projectId": "demo", "clientProfile": "developer", "environment": "prod", "channel": "stable",
              "versionPolicy": "exact", "requestedVersion": "1.0.0", "targetPlatform": platform,
              "installDir": str(client / "apps"), "stateRootDir": str(client / "state"),
              "requireSignedManifests": True, "launchAfterUpdate": True,
              "launchArguments": ["/c", "echo", "UE_DT_FAKE_GAME_OK"] if os.name == "nt" else [],
              "security": {"authenticationMode": "request-signature-v1", "requireHttps": False,
              "credentialName": "device", "allowedDownloadHosts": ["127.0.0.1"],
              "trustedSigningKeys": [{"keyId": "release-1", "publicKeyPath": str(root / "public.pem")}]}}
    log = (root / "server.log").open("w", encoding="utf-8")
    process = subprocess.Popen([server, "serve", "--config", str(root / "server.json")], stdout=log, stderr=log, env=env, creationflags=flags)
    try:
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError("Distribution server exited before ready")
            try:
                urllib.request.urlopen(origin + "/api/v1/catalog", timeout=1)
            except urllib.error.HTTPError as error:
                if error.code == 401:
                    break
            except urllib.error.URLError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Server did not become ready")
        for version in ("1.0.0", "2.0.0"):
            config["requestedVersion"] = version
            write(client / "config.json", config)
            output = run(launcher, "run", "--config", client / "config.json")
            if "UE_DT_FAKE_GAME_OK" not in output:
                raise RuntimeError("Synthetic game execution marker was missing")
        target = client / "apps" / "demo" / "prod" / "stable" / "2.0.0" / platform / "version.txt"
        target.write_text("damaged", encoding="utf-8")
        run(launcher, "run", "--config", client / "config.json", "--repair")
        if target.read_text() != "2.0.0":
            raise RuntimeError("Repair did not restore the expected contents")
        summary = {"platform": platform, "transport": "HTTP request-signature-v1", "published_processes": True,
                   "versions_installed_and_launched": 2, "repair": True, "company_or_unreal_validation": False}
        if args.hold:
            write(root / "summary.json", summary)
            print(f"READY: {root}", flush=True)
            process.wait()
        else:
            run(server, "client-key", "revoke", "--key-id", "pc-test-key", "--config", root / "server.json")
            run(launcher, "run", "--config", client / "config.json", expected=1)
            summary["revoked_key_rejected"] = True
            write(root / "summary.json", summary)
            print(f"PASS: {root}", flush=True)
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)
        log.close()


if __name__ == "__main__":
    main()
