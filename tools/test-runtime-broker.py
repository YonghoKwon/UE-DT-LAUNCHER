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
    root = Path(tempfile.mkdtemp(prefix="uedt-broker-proof-")).resolve()
    suffix = ".exe" if os.name == "nt" else ""
    launcher = root / ("UeDtLauncher" + suffix)
    agent = root / ("UeDtLauncher.Agent" + suffix)
    shutil.copy2(args.launcher, launcher); shutil.copy2(args.agent, agent)
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    app = root / "app"; app.mkdir()
    entry = "game.exe" if os.name == "nt" else "game.sh"
    if os.name == "nt":
        shutil.copy2(Path(os.environ["SystemRoot"]) / "System32" / "cmd.exe", app / entry)
        arguments = ["/c", "ping -n 6 127.0.0.1 > nul"]
    else:
        (app / entry).write_text("#!/bin/sh\nsleep 5\n"); (app / entry).chmod(0o755); arguments = []
    config = {"schemaVersion": 1, "deploymentMode": "managed-agent", "projectId": "demo", "targetPlatform": platform,
              "installDir": str(app), "stateRootDir": str(root / "state"), "logDir": str(root / "logs"), "launchArguments": arguments}
    (root / "config").mkdir()
    state = root / "state" / "demo" / platform; state.mkdir(parents=True)
    manifest = {"appId": "demo", "version": "1.0.0", "platform": platform, "entryPoint": entry,
                "files": [{"path": entry, "size": (app / entry).stat().st_size, "sha256": hashlib.sha256((app / entry).read_bytes()).hexdigest()}]}
    (state / "installed-manifest.json").write_text(json.dumps(manifest))
    backup = state / 'backups' / '20260928000000'; backup.mkdir(parents=True)
    shutil.copy2(app / entry, backup / entry)
    meta = backup / '.uedt-meta'; meta.mkdir()
    (meta / 'backup-info.json').write_text(json.dumps(dict(previousVersion='1.0.0',newVersion='1.0.0',createdAtUtc='2026-09-28T00:00:00Z',addedPaths=[])))
    (meta / 'installed-manifest.json').write_text(json.dumps(manifest))
    endpoint = "uedt-proof-" + uuid.uuid4().hex if os.name == "nt" else str(root / "agent.sock")
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(root), UE_DT_AGENT_ENDPOINT=endpoint)
    from signed_legacy_fixture import configure
    web=root/'web';web.mkdir();shutil.copy2(app/entry,web/entry)
    tls_server,manifest=configure(root,launcher,web,manifest,env,config,'broker')
    # This owned fixture has not started any payload. Let the product create its exact
    # installation identity and valid stopped record; Python must not guess .NET path rules.
    seed=root/'seed-runtime.json';seed.write_text(json.dumps(dict(config,deploymentMode='portable')))
    initialized=subprocess.run([str(launcher),'runtime','recover','--config',str(seed),'--confirm-stopped'],env=env,capture_output=True,timeout=30,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    if initialized.returncode:raise RuntimeError('Owned stopped fixture initialization failed')
    (root/'config'/'launcher.config.json').write_text(json.dumps(config))
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    log = (root / "agent.log").open("w")
    service = subprocess.Popen([str(agent)], env=env, stdout=log, stderr=log, creationflags=flags)
    host = None

    def rpc(command, **fields):
        request = dict(protocolVersion=1, correlationId=uuid.uuid4().hex, command=command, projectId="demo", clientCapabilities=["runtime-supervision-v1"])
        request.update(fields)
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
        from gui_fixture_evidence import wait_agent_ready
        wait_agent_ready(service,endpoint,timeout=30)
        assert "runtime-supervision-v1" in rpc("status")["agentCapabilities"]
        assert rpc('runtime-inspect')['runtime']['state']==0,'Native fixture identity is not quiescent'
        if "rollback-preview-v1" in rpc("status")["agentCapabilities"]:
            preview = rpc("rollback-preview")["rollbackPreview"]
            assert preview['canRestore'] and preview['restoreVersion']=='1.0.0'
            (app / entry).write_bytes(b'damaged')
            mismatch = rpc("rollback",expectedBackupId=preview['backupId'],expectedBackupFingerprint='0'*64)
            assert not mismatch['success'] and mismatch['errorCode']=='backup-preview-changed', \
                'Expected preview rejection; status='+str(mismatch.get('status'))+' code='+str(mismatch.get('errorCode'))
            assert (app / entry).read_bytes()==b'damaged'
            restored = rpc("rollback",expectedBackupId=preview['backupId'],expectedBackupFingerprint=preview['metadataFingerprint'])
            assert restored['success'],restored
            assert hashlib.sha256((app / entry).read_bytes()).hexdigest()==manifest['files'][0]['sha256']
        assert rpc("status", clientCapabilities=[])["success"]
        assert rpc("update", clientCapabilities=[])["status"] == "client-upgrade-required"
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
        for command in ("update", "repair", "rollback"):
            response = rpc(command)
            assert not response["success"], command
        after = {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
                 for folder in (app, state) for p in folder.rglob("*") if p.is_file() and p.name != "update.lock"}
        assert before == after, "Running mutation changed protected files"
        service.terminate(); service.wait(timeout=10)
        assert host.poll() is None, "Runtime host died with Agent"
        service = subprocess.Popen([str(agent)], env=env, stdout=log, stderr=log, creationflags=flags)
        wait_agent_ready(service,endpoint,timeout=30)
        output, error = host.communicate(timeout=20)
        assert host.returncode == 0, error
        assert rpc("runtime-inspect")["runtime"]["state"] == 0
        again = rpc("launch-begin"); assert again["success"]
        host = subprocess.Popen([str(launcher), "runtime-host"], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=flags)
        host.stdin.write(json.dumps({"config": config, "ticket": again["runtimeTicket"], "agentEndpoint": endpoint}) + "\n"); host.stdin.close(); host.stdin=None
        assert json.loads(host.stdout.readline())["state"] == "started"
        host.kill(); host.wait(timeout=5)
        assert rpc("runtime-inspect")["runtime"]["state"] == 3
        assert not rpc("repair")["success"]
        time.sleep(6)  # Owned synthetic descendants exit naturally; no guessed PID cleanup.
        assert rpc("runtime-inspect")["runtime"]["state"] == 3, "Host disappearance incorrectly cleared unknown state"
        summary = {"platform": platform, "same_user_console_agent": True, "wrong_peer_rejected": True,
                   "duplicate_launch_blocked": True, "normal_completion": True, "agent_restart": True,
                   "host_crash_stays_unknown": True, "managed_service_account_verified": False}
        (root / "summary.json").write_text(json.dumps(summary, indent=2))
        print("PASS: " + str(root))
    finally:
        if host and host.poll() is None:
            host.kill(); host.wait(timeout=5)
        if service.poll() is None:
            service.terminate()
            try:service.wait(timeout=10)
            except subprocess.TimeoutExpired:service.kill();service.wait(timeout=5)
        log.close()
        tls_server.shutdown();tls_server.server_close()


if __name__ == "__main__": main()
