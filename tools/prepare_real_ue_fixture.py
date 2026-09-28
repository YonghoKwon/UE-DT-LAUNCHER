"""Opt-in local real-UE acceptance fixture; never installs or runs the UE payload.

Copies published launcher services, archives an existing UE Windows package intact,
and uses the production metadata/intake/approval commands on an isolated loopback
server. The GUI install/run button still requires action-time user confirmation.
All generated credentials, binaries and logs must remain outside Git.
"""
import argparse
import hashlib
import http.server
import json
import os
import re
from pathlib import Path
import shutil
import socket
import subprocess
import threading
import time
import zipfile

from gui_fixture_evidence import inside, sha256, copy_server_support, wait_server_ready, wait_agent_ready, agent_status, verify_files, validate_origin

FLAGS = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0


def fixture_endpoint(root):
    # Parent folders matter: two dated runs may both be named "fixture".
    identity = str(Path(root).resolve()).casefold().encode('utf-8')
    return 'uedt-real-' + hashlib.sha256(identity).hexdigest()[:24]


def validate_package(package, root):
    for candidate in (Path(package).absolute(), Path(root).absolute()):
        for path in (candidate, *candidate.parents):
            if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
                raise ValueError('Linked source/output paths are not accepted')
    package, root = Path(package).resolve(), Path(root).resolve()
    if not package.is_dir() or not (package / 'ma0t10_dt.exe').is_file():
        raise ValueError('Expected an intact ma0t10_dt Windows package')
    if not (package/'ma0t10_dt/Binaries/Win64/ma0t10_dt.exe').is_file() or not (package/'ma0t10_dt/Content/Paks').is_dir():
        raise ValueError('UE payload executable and cooked Paks directory are required')
    if root == package or root.is_relative_to(package):
        raise ValueError('Fixture must not be inside the source package')
    if root.exists() and any(root.iterdir()):
        raise ValueError('Use a new empty fixture; existing data is never replaced')
    for path in package.rglob('*'):
        if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
            raise ValueError('Linked package entries are not accepted')
    return package, root


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2), encoding='utf-8')


def load(root):
    root = Path(root).resolve()
    f = json.loads(inside(root, 'real-fixture.json').read_text())
    if f.get('schemaVersion') != 1 or f.get('kind') != 'real-ue-package':
        raise ValueError('Not a real UE test fixture')
    if f.get('projectId') != 'ma0t10-dt' or not re.fullmatch(r'[0-9A-Za-z][0-9A-Za-z._+-]{0,99}', f.get('version', '')):
        raise ValueError('Unexpected test project/version')
    if not re.fullmatch(r'uedt-real-[A-Za-z0-9-]{1,80}', f.get('endpoint', '')):
        raise ValueError('Not a local real-test Agent endpoint')
    validate_origin(f['origin'])
    for value in f['binaries'].values():
        path = inside(root, value['relativePath'])
        if sha256(path) != value['sha256']:
            raise ValueError('Test cohort binary changed')
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(inside(root, 'client/agent')),
               UE_DT_AGENT_ENDPOINT=f['endpoint'])
    return root, f, env


class OfflineSink(http.server.BaseHTTPRequestHandler):
    # No request headers/body are logged or forwarded. This is not a broker.
    def do_GET(self):
        self.send_response(503)
        self.send_header('Content-Length', '0')
        self.send_header('Connection', 'close')
        self.end_headers()
        self.close_connection = True
    do_POST = do_GET
    def log_message(self, *args):
        pass


def prepare(args):
    if os.name != 'nt':
        raise ValueError('This real-package fixture currently targets Windows only')
    if not args.approve:
        raise ValueError('Explicit --approve is required for isolated test publication')
    package, root = validate_package(args.package, args.root)
    inputs = {name: Path(getattr(args, name)).resolve() for name in ('launcher', 'agent', 'server')}
    if any(not path.is_file() for path in inputs.values()):
        raise ValueError('Published launcher, Agent and server executables are required')
    root.mkdir(parents=True, exist_ok=True)
    endpoint = fixture_endpoint(root)
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(root/'client/agent'), UE_DT_AGENT_ENDPOINT=endpoint)
    binaries = {}
    for name, source in inputs.items():
        target = root / ('client/agent' if name != 'server' else 'cohort/server') / source.name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        binaries[name] = {'relativePath': target.relative_to(root).as_posix(), 'sha256': sha256(target)}
        if name == 'server':
            for key, item in copy_server_support(source, target).items():
                binaries[key] = {'relativePath': Path(item['path']).relative_to(root).as_posix(), 'sha256': item['sha256']}
    exe = lambda name: str(root/binaries[name]['relativePath'])
    counter = 0

    def run(name, *arguments, timeout=900):
        nonlocal counter
        counter += 1
        r = subprocess.run([exe(name), *map(str, arguments)], env=env, capture_output=True,
                           encoding='utf-8', errors='replace', timeout=timeout, creationflags=FLAGS)
        (root/f'command-{counter:02d}.log').write_text(r.stdout+r.stderr, encoding='utf-8')
        if r.returncode:
            raise RuntimeError(f'Command {counter} failed; inspect the private fixture log')
        return r.stdout

    upload = root/'server/incoming/ue-001'
    upload.mkdir(parents=True)
    archive = upload/'Windows.zip'
    files = sorted(path for path in package.rglob('*') if path.is_file())
    print(f'Archiving {len(files)} real package files...', flush=True)
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as output:
        for path in files:
            output.write(path, path.relative_to(package).as_posix())
    run('launcher', 'release-metadata', '--zip', archive, '--project-id', 'ma0t10-dt',
        '--display-name', 'MA0T10 DT — 실제 UE 패키지 시험', '--version', args.version,
        '--platform', 'windows-x64', '--entry-point', 'ma0t10_dt.exe', '--output', upload/'release.json',
        '--notes', '격리된 로컬 시험. 회사 서버 연결 및 정식 운영 승인과 별개입니다.')
    run('launcher', 'generate-signing-key', '--private-key', root/'release.pem', '--public-key', root/'public.pem')
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        origin = f'http://127.0.0.1:{reservation.getsockname()[1]}'
    write(root/'policy.json', {'clients': [{'id':'ue-test-pc','addresses':['127.0.0.1/32'],
         'grants':[{'projectId':'ma0t10-dt','environment':'prod','channel':'stable','versions':[args.version]}]}]})
    write(root/'server.json', {'root':str(root/'server'),'publicUrl':origin,'listenUrl':origin,
        'signingKeyPath':str(root/'release.pem'),'policyPath':str(root/'policy.json'),'authenticationMode':'request-signature-v1'})
    run('launcher','credential','keygen','--name','ue-test-device','--key-id','ue-test-device-key',
        '--public-out',root/'device-public.json','--storage','managed')
    run('server','client-key','add','--client','ue-test-pc','--public-key',root/'device-public.json','--config',root/'server.json')
    job=json.loads(run('server','ingest',upload,'--config',root/'server.json'))
    if job['state']!='pending':
        raise RuntimeError('Real package did not pass intake into approval-pending state')
    run('server','approve',job['id'],'--config',root/'server.json')
    run('launcher','sample-config','--server-url',origin,'--project-id','ma0t10-dt','--profile','developer',
        '--platform','windows-x64','--deployment-mode','managed-agent','--credential-name','ue-test-device',
        '--public-key',root/'public.pem','--output',root/'generated.json')
    sink=http.server.ThreadingHTTPServer(('127.0.0.1',0),OfflineSink)
    sink.daemon_threads=True
    sink_origin=f'http://127.0.0.1:{sink.server_port}'
    config=json.loads((root/'generated.json').read_text(encoding='utf-8-sig'))
    config.update(installDir=str(root/'client/apps'),stateRootDir=str(root/'client/state'),logDir=str(root/'client/logs'),
        versionPolicy='exact',requestedVersion=args.version,launchArguments=[
            '-windowed','-ResX=1280','-ResY=720','-d3d12',f'-UserDir={root}/ue-user/',f'-abslog={root}/ue-runtime.log',
            *[f'-ini:Game:[DTCoreRuntimeOverride]:{key}={sink_origin}' for key in ('BaseApiUrl','LocalApiUrl','TestApiUrl','ProdApiUrl')],
            f'-ini:Game:[DTCoreRuntimeOverride]:WebSocketUrl=ws://127.0.0.1:{sink.server_port}',
            '-ini:Game:[/Script/DTCore.DTCoreSettings]:MaxReconnectAttempts=1'])
    write(root/'client/agent/config/launcher.config.json',config)
    for profile in ('general','developer'):
        gui=dict(config,clientProfile=profile)
        gui['security']=dict(config['security'],credentialName='gui-must-not-read-device-key')
        write(root/f'client/{profile}.json',gui)
    record={'schemaVersion':1,'kind':'real-ue-package','endpoint':endpoint,'binaries':binaries,
        'projectId':'ma0t10-dt','version':args.version,'entryPoint':'ma0t10_dt.exe','packageFiles':len(files),
        'packageBytes':sum(p.stat().st_size for p in files),'zipBytes':archive.stat().st_size,'zipSha256':sha256(archive),
        'approvedJob':job['id'],'origin':origin,'runtimeNetwork':'configured DTCore overrides to owned loopback 503 sink; verify actual UE log after launch',
        'runtimeUserData':str(root/'ue-user'),'launcherSourceHead':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()}
    write(root/'real-fixture.json',record)
    processes=[]; logs=[]
    try:
        threading.Thread(target=sink.serve_forever,daemon=True).start()
        for name, command in [('server',[exe('server'),'serve','--config',str(root/'server.json')]),('agent',[exe('agent')])]:
            log=(root/f'{name}.log').open('w',encoding='utf-8');logs.append(log)
            proc=subprocess.Popen(command,env=env,stdout=log,stderr=log,creationflags=FLAGS);processes.append(proc)
            if name=='server':wait_server_ready(proc,origin)
            else:wait_agent_ready(proc,endpoint)
        if (root/'client/apps').exists():raise RuntimeError('Fixture must not preinstall the UE package')
        print('READY (not installed or launched): '+str(root),flush=True)
        while not (root/'stop-services').exists():
            if any(p.poll() is not None for p in processes):raise RuntimeError('Owned fixture service exited')
            time.sleep(.25)
    finally:
        sink.shutdown();sink.server_close()
        for proc in reversed(processes):
            if proc.poll() is None:
                proc.terminate()
                try:proc.wait(timeout=15)
                except subprocess.TimeoutExpired:proc.kill();proc.wait(timeout=5)
        for log in logs:log.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',required=True)
    sub=parser.add_subparsers(dest='action',required=True)
    prep=sub.add_parser('prepare')
    for key in ('package','launcher','agent','server'):prep.add_argument('--'+key,required=True)
    prep.add_argument('--version',default='0.1.0-ue-test.20260929')
    prep.add_argument('--approve',action='store_true')
    for action in ('status','open-general','open-developer','verify','stop-services'):sub.add_parser(action)
    args=parser.parse_args()
    if args.action=='prepare':return prepare(args)
    root,f,env=load(args.root)
    if args.action.startswith('open-'):
        profile=args.action.removeprefix('open-')
        proc=subprocess.Popen([str(root/f['binaries']['launcher']['relativePath']),'--gui','--config',str(root/f'client/{profile}.json')],env=env)
        print(json.dumps({'guiPid':proc.pid,'profile':profile}))
    elif args.action=='status':
        response=agent_status(f['endpoint'])
        print(json.dumps({'agentReady':response.get('success'),'version':f['version']}))
        for p in (root/'client/state').rglob('runtime-state.json'):
            value=json.loads(p.read_text());print(json.dumps({'runtime':value['state'],'attempt':value.get('attemptId')}))
    elif args.action=='verify':
        relative=f"ma0t10-dt/prod/stable/{f['version']}/windows-x64"
        print('PASS: manifest files='+str(verify_files(inside(root,'client/apps/'+relative),inside(root,'client/state/'+relative+'/installed-manifest.json'))))
    else:
        (root/'stop-services').touch()


if __name__=='__main__':
    main()
