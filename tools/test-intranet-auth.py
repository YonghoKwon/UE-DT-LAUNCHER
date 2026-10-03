#!/usr/bin/env python3
"""Isolated synthetic E2E. Uses published binaries, never a company server.

Outputs contain test keys: keep the root outside source control. Only summary.json
is suitable for redacted evidence. Windows payload is a local copy of cmd.exe;
Linux payload is a harmless marker script, not an Unreal package.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
import struct
import zlib
import hashlib
from gui_fixture_evidence import snapshot_preferences, sha256, wait_agent_ready, wait_server_ready, hold_fixture, verify_cohort, FixtureHarnessLock, copy_server_support
from promotion_fixture_support import promote
from fixture_contract import claim, seal

GUI_LONG_DISPLAY_NAME = "포스코DX 디지털 트윈 통합 운영 시뮬레이터 — 제철 공정·설비 상태·안전 점검 및 원격 협업 시험 프로젝트"
GUI_SECONDARY_DISPLAY_NAME = "포스코DX 보조 검증 프로젝트 — 다중 프로젝트 선택 및 작은 화면 키보드 탐색을 위한 합성 시험"
GUI_LONG_NOTES = (
    "긴 한글 프로젝트명과 여러 줄 릴리스 설명의 줄바꿈·말줄임·키보드 탐색을 확인하기 위한 결정적 합성 시험 데이터입니다. "
    "설비 상태와 공정 흐름을 함께 검토하고, 작업자가 선택한 배포 버전의 설치 상태·진행 단계·문제 해결 안내를 작은 화면에서도 구분할 수 있어야 합니다. "
    "이 프로그램은 실제 회사 설비나 운영 데이터에 연결하지 않으며, 승인된 시험 폴더의 합성 앱과 파일만 사용합니다. "
    "글자 크기와 운영체제 화면 배율이 커져도 핵심 실행 버튼, 현재 버전, 백업 복원 확인과 취소 동작을 사용할 수 있는지 확인합니다."
)

def gui_metadata_arguments(long_labels, version):
    return ['--display-name', GUI_LONG_DISPLAY_NAME, '--notes', '시험 릴리스 '+version+' · '+GUI_LONG_NOTES] if long_labels else []

def fixture_access_policy(long_labels=False):
    grants=[{'projectId':'demo','environment':'prod','channel':'stable','versions':[]}]
    if long_labels:grants.append({'projectId':'demo-secondary','environment':'prod','channel':'stable','versions':['1.0.0']})
    return {'clients':[{'id':'pc-test','addresses':['127.0.0.0/8'],'grants':grants}]}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--server", required=True)
    parser.add_argument("--root")
    parser.add_argument("--hold", action="store_true")
    parser.add_argument("--hold-runtime", action="store_true", help="Keep a synthetic managed game active briefly for GUI inspection")
    parser.add_argument("--service-proof", action="store_true", help="Verify explicit versioned portable service selection using synthetic payloads")
    parser.add_argument("--promotion-proof", action="store_true", help="Verify promotion cannot retarget pending or running managed launches")
    parser.add_argument("--agent")
    parser.add_argument('--developer-launcher',help='Compiled Developer client for edition GUI acceptance')
    parser.add_argument("--prepare-gui", help="Test-only synthetic executable; leave installs empty and hold for GUI")
    parser.add_argument("--nginx", help="Optional isolated Linux nginx executable; no system service changes")
    parser.add_argument("--benchmark", action="store_true", help="Optional synthetic signed load (requires cryptography)")
    parser.add_argument("--operation-proof", action="store_true", help="Noninteractive owned cancellation/status proof; requires Agent")
    parser.add_argument("--credential-proof", action="store_true", help="Published individual revocation and explicit device expiry")
    parser.add_argument("--schedule-proof", action="store_true", help="Published opt-in check has zero installation mutation")
    parser.add_argument("--defer-v2", action="store_true", help="GUI-only: retain v2 pending manual approval")
    parser.add_argument("--defer-promotion", action="store_true", help="GUI-only: approve v1 but leave recommendation unassigned")
    parser.add_argument("--readiness-proof", action="store_true", help="Check published read-only diagnostics and selected release readiness")
    parser.add_argument("--gui-mode", choices=['managed','portable'], default='managed', help="GUI-only deployment, with a fresh isolated credential/install root")
    parser.add_argument("--gui-long-labels", action="store_true", help="GUI-only: deterministic long Korean project name and release notes")
    args = parser.parse_args()
    if args.operation_proof and (not args.agent or args.prepare_gui):
        parser.error("--operation-proof requires console Agent without GUI")
    if args.gui_long_labels and not args.prepare_gui:
        parser.error("--gui-long-labels requires --prepare-gui")
    if args.defer_v2 and not args.prepare_gui:
        parser.error("--defer-v2 requires --prepare-gui")
    if args.prepare_gui and (args.service_proof or args.hold_runtime or args.benchmark or args.nginx):
        parser.error("--prepare-gui cannot combine with runtime/service/load/nginx proofs")
    if args.prepare_gui and args.gui_mode=='managed' and not args.agent:
        parser.error("Managed GUI preparation requires --agent")
    if args.prepare_gui and args.gui_mode=='portable' and args.agent:
        parser.error("Portable GUI preparation must not start or include an Agent")
    if args.gui_mode=='portable' and not args.prepare_gui:
        parser.error("--gui-mode portable requires --prepare-gui")
    launcher, server = str(Path(args.launcher).resolve()), str(Path(args.server).resolve())
    root = Path(args.root).resolve() if args.root else Path(tempfile.mkdtemp(prefix="uedt-intranet-"))
    root.mkdir(parents=True, exist_ok=True)
    if args.prepare_gui and any(root.iterdir()):
        raise RuntimeError('GUI fixtures require a new empty root; existing data will not be replaced')
    if (root / "server.json").exists():
        raise RuntimeError("Use a new isolated root; existing fixture will not be replaced")
    claim(root)
    if args.prepare_gui: snapshot_preferences(root)
    client = root / "client"
    private_root=client / ('portable-private' if args.prepare_gui and args.gui_mode=='portable' else 'agent')
    private_root.mkdir(parents=True)
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(private_root))
    # Existing test override changes storage location only. --storage portable below still
    # exercises current-user credential protection and never touches the user's real keys.
    if args.prepare_gui:
        env['UE_DT_AGENT_ENDPOINT']='uedt-test-'+root.name if os.name=='nt' else str(root/'agent.sock')
    gui_binaries={}
    gui_support_files={}
    if args.prepare_gui:
        for kind, source in [('launcher',launcher),('server',server),('synthetic',args.prepare_gui)]+([('agent',args.agent)] if args.agent else [])+([('developer',args.developer_launcher)] if args.developer_launcher else []):
            destination=(client/('agent' if args.gui_mode=='managed' else 'portable') if kind in ('launcher','agent','developer') else root/'cohort'/kind)/Path(source).name
            destination.parent.mkdir(parents=True,exist_ok=True)
            shutil.copy2(Path(source).resolve(),destination)
            if kind=='server':gui_support_files.update(copy_server_support(source,destination))
            gui_binaries[kind]=str(destination)
        launcher=gui_binaries['launcher'];server=gui_binaries['server']
        args.prepare_gui=gui_binaries['synthetic']
        if args.agent: args.agent=gui_binaries['agent']
    counter = 0
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0

    def run(binary, *arguments, expected=0):
        nonlocal counter
        counter += 1
        result = subprocess.run([binary, *map(str, arguments)], env=env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=120, creationflags=flags)
        sensitive = arguments[:2] in (("credential","set"),("credential","keygen")) or arguments[:1]==("token-issue",)
        (root / f"command-{counter:02d}.log").write_text("sensitive provisioning output omitted\n" if sensitive else result.stdout + result.stderr, encoding="utf-8")
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
    backend = origin
    if args.nginx:
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            backend = f"http://127.0.0.1:{reservation.getsockname()[1]}"
    write(root / "server.json", {"root": str(root / "server"), "publicUrl": origin, "listenUrl": backend,
          "signingKeyPath": str(root / "release.pem"), "policyPath": str(root / "policy.json"),
          "authenticationMode": "request-signature-v1"})
    write(root / "policy.json", fixture_access_policy(args.gui_long_labels))
    seal(root,{'server':server,'launcher':launcher,**({'agent':args.agent} if args.agent else {})},root/'server.json')
    credential_storage='portable' if args.prepare_gui and args.gui_mode=='portable' else 'managed'
    run(launcher, "credential", "keygen", "--name", "device", "--key-id", "pc-test-key", "--public-out", root / "device-public.json", "--storage", credential_storage)
    run(server, "client-key", "add", "--client", "pc-test", "--public-key", root / "device-public.json", "--config", root / "server.json")
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    entry = "game.exe" if os.name == "nt" else "game.sh"
    def png_chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)
    png = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)) + png_chunk(b"IDAT", zlib.compress(b"\0\x14\x30\x55")) + png_chunk(b"IEND", b"")
    pending_jobs = {}
    for version in ("1.0.0", "2.0.0"):
        upload = root / "server" / "incoming" / version
        upload.mkdir(parents=True)
        archive = upload / "Package.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as package:
            if args.prepare_gui:
                package.write(Path(args.prepare_gui).resolve(), entry)
            elif os.name == "nt":
                package.write(Path(os.environ["SystemRoot"]) / "System32" / "cmd.exe", entry)
            else:
                package.writestr(entry, '#!/bin/sh\nsleep "${1:-0}"\nprintf "UE_DT_FAKE_GAME_OK\\n" > runtime-marker.txt\n')
            package.writestr("version.txt", version)
            package.writestr("hero.png", png)
            if args.operation_proof and version == "2.0.0":
                package.writestr("cancellation.bin", b"synthetic-download-" * (4 * 1024 * 1024))
        run(launcher, "release-metadata", "--zip", archive, "--project-id", "demo", "--version", version,
            "--platform", platform, "--entry-point", entry, "--executable-paths", entry,
            "--hero-path", "hero.png", "--output", upload / "release.json", *gui_metadata_arguments(args.gui_long_labels, version))
        job = json.loads(run(server, "ingest", upload, "--config", root / "server.json"))
        if job["state"] != "pending":
            raise RuntimeError(f"Expected pending approval, got {job['state']}")
        if (args.defer_v2 or args.prepare_gui) and version == "2.0.0": pending_jobs[version] = job["id"]
        else:
            run(server, "approve", job["id"], "--config", root / "server.json")
            if not args.defer_promotion: promote(lambda *a: run(server,*a),root/'server.json','demo',version,platform)
    if args.gui_long_labels:
        # View-only stress data: a separately authorized second project, never preinstalled.
        upload=root/'server'/'incoming'/'demo-secondary-1.0.0';upload.mkdir(parents=True)
        archive=upload/'Package.zip';shutil.copy2(root/'server'/'incoming'/'1.0.0'/'Package.zip',archive)
        run(launcher,'release-metadata','--zip',archive,'--project-id','demo-secondary','--version','1.0.0',
            '--platform',platform,'--entry-point',entry,'--executable-paths',entry,'--hero-path','hero.png',
            '--display-name',GUI_SECONDARY_DISPLAY_NAME,'--notes','화면 선택·키보드 탐색 전용 합성 데이터. '+GUI_LONG_NOTES,'--output',upload/'release.json')
        secondary=json.loads(run(server,'ingest',upload,'--config',root/'server.json'))
        if secondary['state']!='pending':raise RuntimeError('Secondary stress release did not await approval')
        run(server,'approve',secondary['id'],'--config',root/'server.json')
        if not args.defer_promotion: promote(lambda *a: run(server,*a),root/'server.json','demo-secondary','1.0.0',platform)
    generated = client / "generated.json"
    run(launcher, "sample-config", "--server-url", origin, "--project-id", "demo", "--profile", "developer", "--platform", platform,
        "--deployment-mode", "portable", "--credential-name", "device", "--public-key", root / "public.pem", "--output", generated)
    unchanged = generated.read_bytes()
    run(launcher, "sample-config", "--output", generated, expected=1)
    if unchanged != generated.read_bytes():
        raise RuntimeError("Existing configuration was overwritten")
    config = json.loads(generated.read_text(encoding="utf-8"))
    config.update(versionPolicy="exact", requestedVersion="1.0.0", installDir=str(client / "apps"), stateRootDir=str(client / "state"),
                  launchArguments=["/c", "echo UE_DT_FAKE_GAME_OK>runtime-marker.txt"] if os.name == "nt" else [])
    if args.operation_proof: config['performance']={'resumeCacheBytes':256*1024*1024}
    harness_lock=FixtureHarnessLock(root) if args.prepare_gui else None
    if harness_lock is not None:harness_lock.__enter__()
    log = (root / "server.log").open("w", encoding="utf-8")
    process = subprocess.Popen([server, "serve", "--config", str(root / "server.json")], stdout=log, stderr=log, env=env, creationflags=flags)
    agent_process = None
    agent_log = None
    nginx_process = None
    nginx_log = None
    try:
        if args.nginx:
            nginx_root = root / "nginx"
            nginx_root.mkdir()
            nginx_config = root / "nginx.conf"
            nginx_config.write_text(f'''pid {root}/nginx.pid;
error_log {root}/nginx-error.log;
events {{ worker_connections 256; }}
http {{
 access_log off;
 client_body_temp_path {root}/nginx/body;
 proxy_temp_path {root}/nginx/proxy;
 fastcgi_temp_path {root}/nginx/fastcgi;
 uwsgi_temp_path {root}/nginx/uwsgi;
 scgi_temp_path {root}/nginx/scgi;
 server {{
  listen 127.0.0.1:{port};
  location ~ ^/(api/v1/(auth/challenge|catalog)|releases/) {{
   proxy_pass {backend};
   proxy_set_header Host $http_host;
   proxy_set_header X-Distribution-Client-IP $remote_addr;
   proxy_set_header X-Forwarded-For "";
   proxy_set_header Forwarded "";
   proxy_set_header X-Original-URI "";
   proxy_cache off;
  }}
  location / {{ return 404; }}
 }}
}}
''', encoding="utf-8")
            run(str(Path(args.nginx).resolve()), "-t", "-p", nginx_root, "-c", nginx_config)
            nginx_log = (root / "nginx.log").open("w")
            nginx_process = subprocess.Popen([str(Path(args.nginx).resolve()), "-p", str(nginx_root), "-c", str(nginx_config), "-g", "daemon off;"], stdout=nginx_log, stderr=nginx_log, env=env)
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError("Distribution server exited before ready")
            try:
                urllib.request.urlopen(origin + "/api/v1/catalog", timeout=1)
            except urllib.error.HTTPError as error:
                if error.code == 401:
                    break
            except (urllib.error.URLError, TimeoutError):
                pass
            time.sleep(0.1)
        else:
            raise RuntimeError("Server did not become ready")
        if not (args.prepare_gui and args.gui_mode=='portable'):
            run(launcher, "doctor", "--config", generated, "--online")
        readiness_proofs=[]
        if args.readiness_proof:
            from readiness_fixture_support import prove_readiness
            readiness_proofs.append(prove_readiness(run,launcher,client,config,args.defer_promotion))
        if not args.prepare_gui:
            if args.readiness_proof: write(root / 'readiness-progress.json', {'phase': 'installation-execution-repair'})
            for version in ("1.0.0", "2.0.0"):
                config["requestedVersion"] = version
                write(client / "config.json", config)
                run(launcher, "run", "--config", client / "config.json")
                runtime = client / "state" / "demo" / "prod" / "stable" / version / platform / "runtime-state.json"
                deadline = time.monotonic() + 15
                while time.monotonic() < deadline:
                    if runtime.exists() and json.loads(runtime.read_text())["state"] == 0:
                        break
                    time.sleep(0.05)
                else:
                    raise RuntimeError("Supervised payload did not complete")
                marker = client / "apps" / "demo" / "prod" / "stable" / version / platform / "runtime-marker.txt"
                if not marker.exists() or "UE_DT_FAKE_GAME_OK" not in marker.read_text():
                    raise RuntimeError("Synthetic game execution marker was missing")
            target = client / "apps" / "demo" / "prod" / "stable" / "2.0.0" / platform / "version.txt"
            target.write_text("damaged", encoding="utf-8")
            run(launcher, "run", "--config", client / "config.json", "--repair")
            if target.read_text() != "2.0.0":
                raise RuntimeError("Repair did not restore the expected contents")
        summary = {"platform": platform, "transport": "HTTP request-signature-v1", "published_processes": True,
                   "versions_installed_and_launched": 2, "repair": True, "nginx": bool(args.nginx), "company_or_unreal_validation": False}
        if args.readiness_proof: summary['readiness_proofs']=readiness_proofs
        if args.service_proof:
            def wait_runtime(version):
                path = client / "state" / "demo" / "prod" / "stable" / version / platform / "runtime-state.json"
                deadline = time.monotonic() + 25
                while time.monotonic() < deadline:
                    if json.loads(path.read_text())["state"] == 0: return
                    time.sleep(.05)
                raise RuntimeError("Service fixture did not finish naturally")
            def service_cycle():
                with (root / ("service-" + config["requestedVersion"] + ".log")).open("w", encoding="utf-8") as output:
                    result = subprocess.run([launcher, "service", "--once", "--config", str(client / "config.json")], env=env, stdout=output, stderr=output, timeout=30, creationflags=flags)
                    if result.returncode != 0: raise RuntimeError("Service fixture cycle failed")
            wait_runtime("2.0.0")
            original_args = config.get("launchArguments")
            config["launchArguments"] = ["/c", "ping -n 11 127.0.0.1 > nul"] if os.name == "nt" else ["10"]
            config["serviceMode"] = dict(autoRestartApp=True, startupGraceSeconds=0)
            config["requestedVersion"] = "1.0.0"; write(client / "config.json", config)
            run(launcher, "runtime", "recover", "--config", client / "config.json", "--version", "1.0.0", "--confirm-stopped", "--service-selection")
            service_cycle()
            old_runtime = client / "state" / "demo" / "prod" / "stable" / "1.0.0" / platform / "runtime-state.json"
            assert json.loads(old_runtime.read_text())["state"] == 2
            service_state = client / "state" / "service" / "demo" / "prod" / "stable" / platform / "service-state.json"
            before = service_state.read_bytes()
            config["requestedVersion"] = "2.0.0"; write(client / "config.json", config)
            run(launcher, "runtime", "recover", "--config", client / "config.json", "--version", "2.0.0", "--confirm-stopped", "--service-selection", expected=1)
            run(launcher, "service", "--once", "--config", client / "config.json", expected=1)
            assert before == service_state.read_bytes()
            wait_runtime("1.0.0")
            run(launcher, "runtime", "recover", "--config", client / "config.json", "--version", "2.0.0", "--confirm-stopped", "--service-selection")
            service_cycle(); wait_runtime("2.0.0")
            assert json.loads(service_state.read_text())["version"] == "2.0.0"
            config["launchArguments"] = original_args; config.pop("serviceMode"); write(client / "config.json", config)
            summary["explicit_service_selection_proof"] = True
        if args.prepare_gui:
            config["launchArguments"] = ["--control-root", str(root / "control")]
            config["requestedVersion"] = "1.0.0"
            summary["versions_installed_and_launched"] = 0; summary["repair"] = False
            summary["gui_prepared_empty"] = True
        if args.agent:
            if args.readiness_proof: write(root / 'readiness-progress.json', {'phase': 'managed-diagnostics'})
            # Match real MSI/RPM layout: the trusted host is beside the Agent, not an arbitrary GUI path.
            composed = client / "agent"
            composed_launcher = composed / Path(launcher).name
            composed_agent = composed / Path(args.agent).name
            if Path(launcher).resolve()!=composed_launcher.resolve(): shutil.copy2(launcher, composed_launcher)
            canonical=Path(launcher).parent/('UeDtLauncher.exe' if os.name=='nt' else 'UeDtLauncher')
            if canonical.is_file() and canonical.name!=Path(launcher).name:shutil.copy2(canonical,composed/canonical.name)
            if Path(args.agent).resolve()!=composed_agent.resolve(): shutil.copy2(Path(args.agent).resolve(), composed_agent)
            launcher = str(composed_launcher)
            config["deploymentMode"] = "managed-agent"
            agent_config = client / "agent" / "config" / "launcher.config.json"
            agent_config.parent.mkdir(parents=True)
            write(agent_config, config)
            env["UE_DT_AGENT_ENDPOINT"] = "uedt-test-" + root.name if os.name == "nt" else str(root / "agent.sock")
            agent_log = (root / "agent.log").open("w", encoding="utf-8")
            agent_process = subprocess.Popen([str(composed_agent)], env=env, stdout=agent_log, stderr=agent_log, creationflags=flags)
            if args.prepare_gui: wait_agent_ready(agent_process,env['UE_DT_AGENT_ENDPOINT'])
            else:
                from gui_fixture_evidence import agent_status
                deadline=time.monotonic()+30
                while True:
                    try:
                        status=agent_status(env['UE_DT_AGENT_ENDPOINT'])
                        if not status.get('success') or 'runtime-supervision-v1' not in status.get('agentCapabilities',[]): raise RuntimeError('Agent capabilities are not ready')
                        break
                    except (OSError,RuntimeError,json.JSONDecodeError):
                        if agent_process.poll() is not None or time.monotonic()>=deadline: raise
                        time.sleep(.1)
            if not args.defer_promotion:
                run(launcher, "agent", "project-asset", "--project", "demo", "--kind", "hero", "--cache", client / "images")
            gui = dict(config, clientProfile="general")
            gui["security"] = dict(config["security"], credentialName="gui-must-not-read-private-key")
            write(client / "general.json", gui)
            gui["clientProfile"] = "developer"
            write(client / "developer.json", gui)
            write(root / "test-environment.json", {"UE_DT_AGENT_DATA_ROOT": env["UE_DT_AGENT_DATA_ROOT"], "UE_DT_AGENT_ENDPOINT": env["UE_DT_AGENT_ENDPOINT"]})
            run(launcher, "doctor", "--config", client / "general.json", "--online")
            if args.readiness_proof:
                readiness_proofs.append(prove_readiness(run,launcher,client,gui,args.defer_promotion))
            summary["managed_asset_and_doctor"] = True
            if args.operation_proof:
                from operation_fixture_support import prove_operations
                summary['owned_operation_cancellation'] = prove_operations(root, client, launcher, env, flags, run, platform)
            if args.promotion_proof:
                import uuid
                from gui_fixture_evidence import agent_status
                config['requestedVersion']='2.0.0'
                config['launchArguments']=['/c','ping -n 8 127.0.0.1 > nul'] if os.name=='nt' else ['7']
                write(agent_config,config)
                selection={'projectId':'demo','environment':'prod','channel':'stable','platform':platform,'version':'2.0.0'}
                response=agent_status(env['UE_DT_AGENT_ENDPOINT'],{'protocolVersion':1,'correlationId':uuid.uuid4().hex,'command':'launch-begin',
                    'projectId':'demo','selection':selection,'clientCapabilities':['runtime-supervision-v1','runtime-data-v1']})
                if not response.get('success'):raise RuntimeError('Pending launch proof failed')
                state=client/'state/demo/prod/stable/2.0.0'/platform/'runtime-state.json'
                before=state.read_bytes()
                promote(lambda *a:run(server,*a),root/'server.json','demo','1.0.0',platform,'promotion during pending launch')
                assert before==state.read_bytes()
                with subprocess.Popen([launcher,'runtime-host'],env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,creationflags=flags) as host:
                    host.stdin.write(json.dumps({'config':config,'ticket':response['runtimeTicket'],'agentEndpoint':env['UE_DT_AGENT_ENDPOINT'],'selection':selection})+'\n');host.stdin.close()
                    started=json.loads(host.stdout.readline());assert started['state']=='started'
                    live=json.loads(state.read_text());assert live['state']==2 and live['entryPoint'].endswith('game.exe' if os.name=='nt' else 'game.sh')
                    before=state.read_bytes()
                    promote(lambda *a:run(server,*a),root/'server.json','demo','2.0.0',platform,'promotion during active launch')
                    assert before==state.read_bytes()
                    host.wait(timeout=25);assert host.returncode==0 and json.loads(state.read_text())['state']==0
                summary['promotion_preserves_pending_and_running_launch']=True
            if args.hold_runtime:
                config["launchArguments"] = ["/c", "ping -n 61 127.0.0.1 > nul"] if os.name == "nt" else ["60"]
                write(agent_config, config)
                run(launcher, "run", "--config", client / "general.json")
                summary["managed_runtime_started_for_gui"] = True
        if args.prepare_gui:
            if args.gui_mode=='portable':
                config['deploymentMode']='portable'
                for profile in ('general','developer'): write(client/(profile+'.json'),dict(config,clientProfile=profile))
                write(root/'test-environment.json',{'UE_DT_AGENT_DATA_ROOT':env['UE_DT_AGENT_DATA_ROOT'],'UE_DT_AGENT_ENDPOINT':env['UE_DT_AGENT_ENDPOINT']})
                run(launcher,'credential','status','--name','device','--storage','portable')
                # No Agent request or managed doctor in this preparation branch.
                summary['portable_credential_ready']=True
            control=root/'control';control.mkdir(exist_ok=True)
            shutil.copy2(root/'policy.json',control/'original-policy.json')
            shutil.copy2(root/'public.pem',control/'original-public.pem')
            source_root=Path(__file__).resolve().parent.parent
            source_diff=subprocess.check_output(['git','diff','HEAD','--','src','tools'],cwd=source_root)
            untracked=subprocess.check_output(['git','ls-files','--others','--exclude-standard','--','src','tools'],cwd=source_root,text=True).splitlines()
            source_evidence=source_diff+b''.join(name.encode()+b'\0'+(source_root/name).read_bytes() for name in sorted(untracked) if (source_root/name).is_file())
            write(root/'fixture.json',{'schemaVersion':2,'id':root.name,'deploymentMode':args.gui_mode,'platform':platform,'guiLongLabels':args.gui_long_labels,
                  'sourceHead':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source_root,text=True).strip(),
                  'sourceDiffSha256':hashlib.sha256(source_evidence).hexdigest(),'sourceDirty':bool(source_evidence),
                  'binaries':{kind:{'path':value,'sha256':sha256(value)} for kind,value in gui_binaries.items()},
                  'supportFiles':gui_support_files,'pendingJobs':pending_jobs,
                  'viewOnlyProjects':['demo-secondary'] if args.gui_long_labels else []})
            summary['deployment_mode']=args.gui_mode
            summary['gui_long_labels']=args.gui_long_labels
            summary['view_only_projects']=['demo-secondary'] if args.gui_long_labels else []
            verify_cohort(root);wait_server_ready(process,origin)
        if args.benchmark:
            from benchmark_intranet_auth import run_load
            load = run_load(origin, root, lambda path: run(server, "client-key", "add", "--client", "pc-test", "--public-key", path, "--config", root / "server.json"), platform, process.pid)
            summary["load_failed_requests"] = sum(row["failed_requests"] for row in load["results"])
        if args.credential_proof:
            run(launcher, 'credential', 'keygen', '--name', 'expired-device', '--key-id', 'expired-key', '--public-out', root/'expired-public.json')
            run(server, 'client-key', 'add', '--client', 'pc-test', '--public-key', root/'expired-public.json', '--expires-at', '2000-01-01T00:00:00Z', '--config', root/'server.json')
            expired=dict(config,deploymentMode='portable',security=dict(config['security'],credentialName='expired-device'))
            write(client/'expired.json',expired)
            run(launcher, 'doctor', '--config', client/'expired.json', '--online', expected=1)
            listed=json.loads(run(server,'client-key','list','--config',root/'server.json'))
            if not next(k for k in listed if k['keyId']=='expired-key')['expired']:raise RuntimeError('Expiry was not durably latched')
            token=run(server,'token-issue','pc-test','--config',root/'server.json').strip()
            rows=json.loads(run(server,'token-list','--config',root/'server.json'))
            if any(token in json.dumps(row) for row in rows):raise RuntimeError('Management listing disclosed token')
            run(server,'token-revoke-id','--id',rows[-1]['id'],'--config',root/'server.json')
            if not json.loads(run(server,'token-list','--config',root/'server.json'))[-1]['revoked']:raise RuntimeError('Individual revoke failed')
            summary['credential_lifecycle']=True
        if args.schedule_proof:
            from scheduled_fixture_support import prove_schedule
            summary['read_only_scheduled_check']=prove_schedule(run,launcher,client,config)
        if args.hold or args.prepare_gui:
            write(root / "summary.json", summary)
            print(f"READY: {root}", flush=True)
            if args.prepare_gui:
                hold_fixture(root,verify_cohort(root),env,process,agent_process,log,agent_log)
            else:
                process.wait()
        else:
            if args.readiness_proof: write(root / 'readiness-progress.json', {'phase': 'revocation'})
            run(server, "client-key", "revoke", "--key-id", "pc-test-key", "--config", root / "server.json")
            if args.readiness_proof:
                report=json.loads(run(launcher,'doctor','--config',client/'readiness-config.json','--online',expected=1))
                assert any(c['code']=='authentication-failed' for c in report['checks'])
                summary['readiness_revocation_rejected']=True
            run(launcher, "run", "--config", client / "config.json", expected=1)
            summary["revoked_key_rejected"] = True
            run(launcher, "credential", "delete", "--name", "device")
            run(launcher, "credential", "status", "--name", "device", expected=1)
            summary["local_key_deleted"] = True
            write(root / "summary.json", summary)
            print(f"PASS: {root}", flush=True)
    finally:
        if nginx_process is not None and nginx_process.poll() is None:
            nginx_process.terminate(); nginx_process.wait(timeout=10)
        if nginx_log is not None:
            nginx_log.close()
        if agent_process is not None and agent_process.poll() is None:
            agent_process.terminate(); agent_process.wait(timeout=10)
        if agent_log is not None:
            agent_log.close()
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=5)
        log.close()
        if harness_lock is not None:harness_lock.close()


if __name__ == "__main__":
    main()
