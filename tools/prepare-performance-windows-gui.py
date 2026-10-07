#!/usr/bin/env python3
"""Prepare an isolated Windows GUI/Agent fixture against a held Linux HTTPS E2E server.

Run the Linux test-distribution-e2e.sh with UE_DT_E2E_HOLD=1 first. Example:
  python tools/prepare-performance-windows-gui.py setup --linux-root /tmp/uedt-distribution-e2e.ABC123 \
    --linux-server /tmp/published-server/UeDtLauncher.DistributionServer \
    --client publish/windows-client --agent publish/windows-agent --output publish/gui-evidence
  python tools/prepare-performance-windows-gui.py start --output publish/gui-evidence --profile developer

setup publishes Windows demo 1.0.0 and 2.0.0, with ten deterministic 1 MiB data
files (nine unchanged) and the built-in Windows whoami.exe as a harmless entry
point. It does not execute the package. start opens the real GUI and a hidden,
ordinary Agent process; it never installs/changes a Windows service or automates
GUI input. start is one-shot per output directory. Close the GUI normally and
stop only the recorded Agent PID after checking its process identity manually.
The token goes directly from captured CLI output into a scoped credential-set
environment and is never printed or written to logs/context JSON.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid
import zipfile


ROOT_PATTERN = re.compile(r"/tmp/uedt-distribution-e2e\.[A-Za-z0-9]+\Z")
MARKER = "ue-dt-windows-gui-performance-v1"
MAX_DOCUMENT = 1024 * 1024


def write_json_new(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def execute(command, *, env=None, log=None, label="fixture command", timeout=120):
    # Never embed commands in a shell: Windows executable paths and WSL paths are argv entries.
    try:
        result = subprocess.run([str(value) for value in command], env=env,
                                stdin=subprocess.DEVNULL, capture_output=True,
                                timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise RuntimeError(f"{label} could not complete ({type(error).__name__}); no command output was printed") from None
    if log is not None:
        with log.open("xb") as stream:
            stream.write(result.stdout)
            stream.write(result.stderr)
    if result.returncode:
        raise RuntimeError(f"{label} failed with exit code {result.returncode}; no command output was printed")
    return result.stdout


def decode(data):
    return data.decode("utf-8-sig").strip()


def windows_executable(value, filename):
    path = value.resolve(strict=True)
    if path.is_dir():
        path = (path / filename).resolve(strict=True)
    if not path.is_file() or path.suffix.lower() != ".exe":
        raise ValueError("Expected a published Windows executable: " + filename)
    return path


def scoped_environment(root, endpoint):
    env = dict(os.environ)
    env.pop("UE_DT_CREDENTIAL_TOKEN", None)
    env["UE_DT_AGENT_DATA_ROOT"] = str(root)
    env["UE_DT_AGENT_ENDPOINT"] = endpoint
    return env


class Wsl:
    def __init__(self, distribution):
        self.prefix = ["wsl.exe"] + (["--distribution", distribution] if distribution else []) + ["--exec"]

    def run(self, *args, **kwargs):
        return execute([*self.prefix, *args], **kwargs)

    def document(self, path):
        # Bounded, read-only reads of public fixture material; never read the private signing key.
        value = self.run("head", "-c", str(MAX_DOCUMENT + 1), "--", path, label="read Linux fixture")
        if len(value) > MAX_DOCUMENT:
            raise ValueError("Linux fixture document exceeds 1 MiB")
        return value

    def windows_path(self, path):
        return decode(self.run("wslpath", "-a", "-u", str(path), label="resolve Windows fixture path"))


def validate_linux_fixture(wsl, root, server):
    if not ROOT_PATTERN.fullmatch(root):
        raise ValueError("--linux-root must name /tmp/uedt-distribution-e2e.<suffix>")
    if decode(wsl.run("realpath", "--", root)) != root:
        raise ValueError("Linux fixture root must not be a symbolic link")
    if not server.startswith("/") or "\x00" in server:
        raise ValueError("--linux-server must be an absolute WSL executable path")
    wsl.run("test", "-x", server, label="validate published Linux server")
    for name in ("server.json", "policy.json", "public.pem", "tls.crt"):
        if decode(wsl.run("realpath", "--", root + "/" + name)) != root + "/" + name:
            raise ValueError("Fixture public/configuration files must not be symbolic links")
    config = json.loads(wsl.document(root + "/server.json"))
    if (config.get("root") != root + "/server" or config.get("policyPath") != root + "/policy.json"
            or config.get("signingKeyPath") != root + "/sign.pem"
            or config.get("publicUrl") != "https://localhost:19443"
            or config.get("listenUrl") != "http://127.0.0.1:18520"):
        raise ValueError("Configuration does not match the isolated Linux HTTPS E2E fixture")
    for path in (root + "/server", root + "/server/incoming", root + "/sign.pem"):
        if decode(wsl.run("realpath", "--", path)) != path:
            raise ValueError("Fixture server, incoming and signing-key paths must remain inside the fixture")
    policy = json.loads(wsl.document(root + "/policy.json"))
    clients = policy.get("clients", [])
    if len(clients) != 1 or clients[0].get("id") != "pc-a" or clients[0].get("addresses") != ["127.0.0.1"]:
        raise ValueError("Expected the existing loopback-only pc-a E2E identity")
    if clients[0].get("grants") != [{"projectId": "demo", "environment": "prod", "channel": "stable", "versions": []}]:
        raise ValueError("Expected the existing demo/prod/stable E2E grant")
    return config


def make_zip(path, executable, version):
    with zipfile.ZipFile(path, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        def add(name, data):
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, data)
        add("whoami.exe", executable.read_bytes())
        for index in range(10):
            generation = version if index == 9 else "shared"
            data = hashlib.shake_256(f"ue-dt-gui-fixture/{generation}/{index}".encode()).digest(1024 * 1024)
            add(f"data/chunk-{index:02}.bin", data)


def setup(args):
    client = windows_executable(args.client, "UeDtLauncher.exe")
    agent = windows_executable(args.agent, "UeDtLauncher.Agent.exe")
    wsl = Wsl(args.distribution)
    validate_linux_fixture(wsl, args.linux_root, args.linux_server)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / "logs").mkdir()
    endpoint = "UeDtLauncher.Perf." + uuid.uuid4().hex
    env = scoped_environment(output, endpoint)
    for name in ("public.pem", "tls.crt"):
        data = wsl.document(args.linux_root + "/" + name)
        if b"PRIVATE KEY" in data:
            raise ValueError("Refusing private-key material in public fixture files")
        with (output / name).open("xb") as stream:
            stream.write(data)
    whoami = (Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "whoami.exe").resolve(strict=True)
    published = []
    for number, version in enumerate(("1.0.0", "2.0.0"), start=1):
        package = output / "packages" / version
        package.mkdir(parents=True)
        make_zip(package / "Windows.zip", whoami, version)
        execute([client, "release-metadata", "--zip", package / "Windows.zip", "--project-id", "demo",
                 "--display-name", "Windows HTTPS GUI fixture", "--version", version, "--platform", "windows-x64",
                 "--entry-point", "whoami.exe", "--output", package / "release.json"], env=env,
                log=output / "logs" / f"metadata-{version}.log", label="generate external Windows release metadata")
        incoming = args.linux_root + "/server/incoming/job-win" + str(number)
        # mkdir without -p makes existing uploads a hard failure; never overwrite another fixture.
        wsl.run("mkdir", "--", incoming, label="create fresh Windows upload directory")
        for name in ("Windows.zip", "release.json"):
            wsl.run("cp", "--no-clobber", "--", wsl.windows_path(package / name), incoming + "/" + name,
                    label="copy Windows fixture upload")
        job_document = wsl.run(args.linux_server, "ingest", incoming, "--config", args.linux_root + "/server.json",
                               log=output / "logs" / f"ingest-{version}.json", label="ingest Windows fixture", timeout=300)
        job = json.loads(job_document)
        if job.get("state") != "pending" or not re.fullmatch(r"[0-9a-f]{24}", job.get("id", "")):
            raise RuntimeError("Windows fixture did not reach pending approval; inspect the isolated ingest log")
        wsl.run(args.linux_server, "approve", job["id"], "--config", args.linux_root + "/server.json",
                log=output / "logs" / f"approve-{version}.log", label="approve Windows fixture", timeout=300)
        published.append({"version": version, "jobId": job["id"]})
    token = decode(wsl.run(args.linux_server, "token-issue", "pc-a", "--config", args.linux_root + "/server.json",
                           label="issue isolated pc-a credential"))
    if not re.fullmatch(r"[0-9a-f]{64}", token):
        raise RuntimeError("Unexpected token response; credential output was not saved")
    try:
        env["UE_DT_CREDENTIAL_TOKEN"] = token
        execute([client, "credential", "set", "--name", "windows-gui-e2e"], env=env,
                label="store isolated Windows credential")
    finally:
        env.pop("UE_DT_CREDENTIAL_TOKEN", None)
        token = None
    common = {
        "schemaVersion": 2, "deploymentMode": "managed-agent", "distributionServerUrl": "https://localhost:19443",
        "projectId": "demo", "clientProfile": "general", "environment": "prod", "channel": "stable",
        "versionPolicy": "latest", "targetPlatform": "windows-x64", "requireSignedManifests": True,
        "installDir": str(output / "apps"), "stateRootDir": str(output / "state"), "logDir": str(output / "logs"),
        "launchAfterUpdate": False,
        "performance": {"downloadConcurrency": 2, "hashConcurrency": 2, "reusePreviousInstallations": True},
        "security": {"requireHttps": True, "credentialName": "windows-gui-e2e", "customCaCertificatePath": str(output / "tls.crt"),
                     "allowedDownloadHosts": ["localhost"],
                     "trustedSigningKeys": [{"keyId": "release-1", "publicKeyPath": str(output / "public.pem")}]},
        "projects": [{"projectId": "demo", "displayName": "Windows HTTPS GUI fixture",
                      "description": "Synthetic fixture; not Unreal Engine or a Windows service installation.",
                      "visibleToProfiles": ["general", "developer"]}],
    }
    write_json_new(output / "config" / "launcher.config.json", common)
    write_json_new(output / "gui-general.json", common)
    write_json_new(output / "gui-developer.json", {**common, "clientProfile": "developer", "versionPolicy": "exact", "requestedVersion": "1.0.0"})
    context = {"marker": MARKER, "root": str(output), "client": str(client), "agent": str(agent),
               "endpoint": endpoint, "linuxRoot": args.linux_root, "published": published,
               "managedConfig": str(output / "config" / "launcher.config.json")}
    write_json_new(output / "context.json", context)
    print(json.dumps({"root": str(output), "managedConfig": context["managedConfig"],
                      "generalConfig": str(output / "gui-general.json"), "developerConfig": str(output / "gui-developer.json")}, indent=2))


def start(args):
    output = args.output.resolve(strict=True)
    context = json.loads((output / "context.json").read_text(encoding="utf-8"))
    if context.get("marker") != MARKER or context.get("root") != str(output):
        raise ValueError("Output is not an owned Windows GUI fixture")
    client = windows_executable(Path(context["client"]), "UeDtLauncher.exe")
    agent = windows_executable(Path(context["agent"]), "UeDtLauncher.Agent.exe")
    gui_config = output / ("gui-" + args.profile + ".json")
    env = scoped_environment(output, context["endpoint"])
    # Exclusive one-shot marker avoids racing launches and avoids killing a reused PID later.
    with (output / "started.json").open("x", encoding="utf-8") as receipt:
        agent_process = gui_process = None
        try:
            with (output / "logs" / "agent-console.log").open("xb") as agent_log:
                agent_process = subprocess.Popen([str(agent)], cwd=output, env=env, stdin=subprocess.DEVNULL,
                                                 stdout=agent_log, stderr=subprocess.STDOUT,
                                                 creationflags=subprocess.CREATE_NO_WINDOW)
            time.sleep(.75)
            if agent_process.poll() is not None:
                raise RuntimeError("Isolated Agent exited; inspect agent-console.log")
            with (output / "logs" / "gui-console.log").open("xb") as gui_log:
                # Published GUI is intentionally visible; no UI automation, hidden-window flag or service control.
                gui_process = subprocess.Popen([str(client), "--gui", "--config", str(gui_config)], cwd=output,
                                               env=env, stdin=subprocess.DEVNULL, stdout=gui_log, stderr=subprocess.STDOUT)
            result = {"root": str(output), "profile": args.profile, "agentPid": agent_process.pid,
                      "guiPid": gui_process.pid, "endpoint": context["endpoint"], "guiConfig": str(gui_config),
                      "agentExecutable": str(agent), "guiExecutable": str(client)}
            json.dump(result, receipt, indent=2)
            print(json.dumps(result, indent=2))
        except BaseException:
            for process in (gui_process, agent_process):
                if process is not None and process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)
            raise


def main():
    if os.name != "nt":
        raise RuntimeError("Run this preparation helper on Windows, with WSL available")
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("setup")
    prepare.add_argument("--linux-root", required=True)
    prepare.add_argument("--linux-server", required=True)
    prepare.add_argument("--distribution", help="Optional WSL distribution; otherwise use its configured default")
    prepare.add_argument("--client", required=True, type=Path)
    prepare.add_argument("--agent", required=True, type=Path)
    prepare.add_argument("--output", required=True, type=Path)
    launch = commands.add_parser("start")
    launch.add_argument("--output", required=True, type=Path)
    launch.add_argument("--profile", choices=("general", "developer"), default="general")
    args = parser.parse_args()
    setup(args) if args.command == "setup" else start(args)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        print("Preparation failed: " + str(error), file=sys.stderr)
        raise SystemExit(1)
