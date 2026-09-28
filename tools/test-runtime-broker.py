"""Published console-Agent registration proof. No services/accounts or company apps."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import tempfile
import time
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--agent", required=True)
    args = parser.parse_args()
    root = Path(tempfile.mkdtemp(prefix="uedt-broker-proof-"))
    suffix = ".exe" if os.name == "nt" else ""
    launcher = root / ("UeDtLauncher" + suffix)
    agent = root / ("UeDtLauncher.Agent" + suffix)
    shutil.copy2(args.launcher, launcher); shutil.copy2(args.agent, agent)
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    app = root / "app"; app.mkdir()
    entry = "game.exe" if os.name == "nt" else "game.sh"
    if os.name == "nt":
        shutil.copy2(Path(os.environ["SystemRoot"]) / "System32" / "cmd.exe", app / entry)
        arguments = ["/c", "ping -n 4 127.0.0.1 > nul"]
    else:
        (app / entry).write_text("#!/bin/sh\nsleep 3\n"); (app / entry).chmod(0o755); arguments = []
    config = {"schemaVersion": 1, "deploymentMode": "managed-agent", "projectId": "demo", "targetPlatform": platform,
              "installDir": str(app), "stateRootDir": str(root / "state"), "logDir": str(root / "logs"), "launchArguments": arguments}
    (root / "config").mkdir()
    (root / "config" / "launcher.config.json").write_text(json.dumps(config))
    state = root / "state" / "demo" / platform; state.mkdir(parents=True)
    manifest = {"appId": "demo", "version": "1.0.0", "platform": platform, "entryPoint": entry,
                "files": [{"path": entry, "size": (app / entry).stat().st_size, "sha256": hashlib.sha256((app / entry).read_bytes()).hexdigest()}]}
    (state / "installed-manifest.json").write_text(json.dumps(manifest))
    canonical = str(app).upper() if os.name == "nt" else str(app)
    (state / "runtime-state.json").write_text(json.dumps({"schemaVersion": 1, "installationId": hashlib.sha256(canonical.encode()).hexdigest(), "state": 0, "origin": "fixture-confirmed"}))
    endpoint = "uedt-proof-" + uuid.uuid4().hex if os.name == "nt" else str(root / "agent.sock")
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(root), UE_DT_AGENT_ENDPOINT=endpoint)
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    log = (root / "agent.log").open("w")
    service = subprocess.Popen([str(agent)], env=env, stdout=log, stderr=log, creationflags=flags)
    host = None

    def rpc(command, **fields):
        request = dict(protocolVersion=1, correlationId=uuid.uuid4().hex, command=command, projectId="demo", clientCapabilities=["runtime-supervision-v1"], **fields)
        if os.name == "nt":
            deadline = time.monotonic() + 5
            while True:
                try:
                    connection = open("\\\\.\\pipe\\" + endpoint, "r+b", buffering=0)
                    break
                except OSError:
                    if time.monotonic() >= deadline: raise
                    time.sleep(0.02)
        else:
            channel = socket.socket(socket.AF_UNIX); channel.settimeout(10); channel.connect(endpoint); connection = channel.makefile("rwb", buffering=0)
        try:
            data = json.dumps(request).encode(); connection.write(struct.pack("<i", len(data)) + data)
            length = struct.unpack("<i", connection.read(4))[0]
            body = bytearray()
            while len(body) < length:
                chunk = connection.read(length - len(body))
                if not chunk:
                    raise RuntimeError("Incomplete IPC response")
                body.extend(chunk)
            response = json.loads(body)
            assert response["correlationId"] == request["correlationId"]
            return response
        finally:
            connection.close()
            if os.name != "nt": channel.close()
    try:
        deadline = time.monotonic() + 15
        if os.name != "nt":
            while not Path(endpoint).exists() and service.poll() is None and time.monotonic() < deadline:
                time.sleep(0.05)
        else:
            time.sleep(1)
        assert "runtime-supervision-v1" in rpc("status")["agentCapabilities"]
        begun = rpc("launch-begin")
        assert begun["success"], begun["message"]
        ticket = begun["runtimeTicket"]
        # A real, same-user Python peer cannot pretend to be the expected runtime executable.
        assert not rpc("launch-attach", runtimeTicket=ticket)["success"]
        host = subprocess.Popen([str(launcher), "runtime-host"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=flags)
        host.stdin.write(json.dumps({"config": config, "ticket": ticket, "agentEndpoint": endpoint}) + "\n"); host.stdin.close(); host.stdin=None
        event = json.loads(host.stdout.readline())
        assert event["state"] == "started", event
        assert rpc("runtime-inspect")["runtime"]["state"] == 2
        assert not rpc("launch-begin")["success"]
        assert not rpc("launch-complete", runtimeTicket=ticket)["success"]
        before = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                  for folder in (app, state) for p in folder.rglob("*") if p.is_file() and p.name != "update.lock"}
        for command in ("update", "repair"):
            response = rpc(command)
            assert not response["success"], command
        after = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                 for folder in (app, state) for p in folder.rglob("*") if p.is_file() and p.name != "update.lock"}
        assert before == after, "Running mutation changed protected files"
        output, error = host.communicate(timeout=20)
        assert host.returncode == 0, error
        assert rpc("runtime-inspect")["runtime"]["state"] == 0
        summary = {"platform": platform, "same_user_console_agent": True, "wrong_peer_rejected": True,
                   "duplicate_launch_blocked": True, "normal_completion": True, "managed_service_account_verified": False}
        (root / "summary.json").write_text(json.dumps(summary, indent=2))
        print("PASS: " + str(root))
    finally:
        if host and host.poll() is None:
            host.kill(); host.wait(timeout=5)
        service.terminate(); service.wait(timeout=10); log.close()


if __name__ == "__main__": main()
