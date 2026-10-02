"""Isolated v1/v2 UE-containing fixtures. No UE source edits or preinstallation.

Both releases use the SAME real UE executable/cooked files. Only non-executable
FixtureAcceptance text members differ. This is NOT two independently built UE versions.
Generated keys, packages, installations and raw evidence stay outside Git.
"""
import argparse
import hashlib
import http.server
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import threading
import time
import zipfile
from contextlib import contextmanager

from prepare_real_ue_fixture import validate_package, fixture_endpoint, write, OfflineSink, FLAGS
from gui_fixture_evidence import (inside, sha256, copy_server_support, wait_server_ready,
    wait_agent_ready, agent_status, verify_files, validate_origin, FixtureHarnessLock)

TEST_FILES = {'FixtureAcceptance/changed.txt', 'FixtureAcceptance/v1-only.txt', 'FixtureAcceptance/v2-only.txt'}
COHORT_FILES = {'launcher': 'UeDtLauncher.exe', 'agent': 'UeDtLauncher.Agent.exe',
                'server': 'UeDtLauncher.DistributionServer.exe'}


def inventory(folder):
    result = {}
    for p in sorted(Path(folder).rglob('*')):
        if p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()):
            raise ValueError('Linked inventory entries are not accepted')
        if p.is_file(): result[p.relative_to(folder).as_posix()] = {'size': p.stat().st_size, 'sha256': sha256(p)}
    return result


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def validate_versions(versions):
    if len(versions) != 2 or versions[0] == versions[1] or any(
        not re.fullmatch(r'[0-9A-Za-z][0-9A-Za-z._+-]{0,99}', v) or v in ('.', '..') for v in versions):
        raise ValueError('Two distinct safe release versions are required')


def validate_cohort(path, inputs):
    value = json.loads(Path(path).read_text())
    if value.get('schemaVersion') != 1 or value.get('rid') != 'win-x64' or value.get('productSourceDirty') is not False:
        raise ValueError('Frozen clean-source Windows cohort required')
    if not re.fullmatch(r'[0-9a-f]{40}', value.get('sourceHead', '')) or not re.fullmatch(r'[0-9a-f]{64}', value.get('productSourceHash', '')):
        raise ValueError('Missing publication source evidence')
    for role, source in inputs.items():
        recorded = value['binaries'][role]
        if Path(recorded['path']).resolve() != source or sha256(source) != recorded['sha256']:
            raise ValueError('Input is not the recorded published artifact')
    return value


def archive_release(package, archive, version, index, expected):
    # Never mutate/copy back into the input. Stream original payload into each ZIP.
    if any(p.startswith('FixtureAcceptance/') for p in expected):
        raise ValueError('Source package already contains acceptance-only files')
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as output:
        for name in expected: output.write(inside(package, name), name)
        output.writestr('FixtureAcceptance/changed.txt', 'acceptance release '+version+'\n')
        output.writestr('FixtureAcceptance/v'+str(index)+'-only.txt', 'test file, not UE content\n')
    if inventory(package) != expected:
        raise ValueError('Input changed during archiving; do not approve the fixture')
    with zipfile.ZipFile(archive) as source:
        names = source.namelist()
        if len(names) != len(expected)+2 or len(set(names)) != len(names):
            raise ValueError('Archive membership mismatch')
        for name, item in expected.items():
            with source.open(name) as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            if source.getinfo(name).file_size != item['size'] or digest != item['sha256']:
                raise ValueError('Archive payload differs from its source')


def command(root, f, env, name, *arguments):
    result = subprocess.run([str(inside(root, f['binaries'][name]['relativePath'])), *map(str, arguments)],
        env=env, capture_output=True, encoding='utf-8', errors='replace', timeout=1200, creationflags=FLAGS)
    logs = inside(root, 'private-commands'); logs.mkdir(exist_ok=True)
    (logs/(str(time.time_ns())+'.log')).write_text(result.stdout+result.stderr, encoding='utf-8')
    if result.returncode: raise RuntimeError('Published command failed; inspect private command log')
    return result.stdout


def load(root):
    root = Path(root).absolute()
    # Check root itself before resolving; do not follow a replaced fixture root.
    for p in (root, *root.parents):
        if p.is_symlink() or (hasattr(p, 'is_junction') and p.is_junction()): raise ValueError('Linked fixture root')
    root = root.resolve()
    f = json.loads(inside(root, 'versioned-real-fixture.json').read_text())
    if f.get('schemaVersion') != 2 or f.get('kind') != 'same-ue-payload-file-delta' or f.get('projectId') != 'ma0t10-dt':
        raise ValueError('Not a versioned real UE fixture')
    if f.get('mode') not in ('managed', 'portable'): raise ValueError('Unsupported fixture mode')
    validate_versions(f.get('versions', []))
    if f.get('endpoint') != fixture_endpoint(root): raise ValueError('Endpoint does not belong to this fixture')
    validate_origin(f['origin']); validate_origin(f['sinkOrigin'])
    required = {'launcher', 'server'} | ({'agent'} if f['mode'] == 'managed' else set())
    if not required <= set(f['binaries']): raise ValueError('Missing published cohort member')
    for name, value in f['binaries'].items():
        path = inside(root, value['relativePath'])
        if name in COHORT_FILES and path.name != COHORT_FILES[name]: raise ValueError('Wrong cohort executable')
        if sha256(path) != value['sha256']: raise ValueError('Published cohort changed')
    config = json.loads(inside(root, 'server.json').read_text())
    if config['publicUrl'] != f['origin'] or config['listenUrl'] != f['origin']: raise ValueError('Server origin changed')
    for field in ('root', 'signingKeyPath', 'policyPath'):
        inside(root, Path(config[field]).relative_to(root))
    for profile in ('general', 'developer'):
        c = json.loads(inside(root, 'client/'+profile+'.json').read_text())
        if c['projectId'] != f['projectId'] or c['distributionServerUrl'] != f['origin']: raise ValueError('Client identity changed')
        for field in ('installDir', 'stateRootDir', 'logDir'):
            inside(root, Path(c[field]).relative_to(root))
    if f.get('runtimeDataEnabled'):
        for profile in ('general', 'developer'):
            c = json.loads(inside(root, 'client/'+profile+'.json').read_text())
            if c.get('runtimeData') != {'enabled': True, 'adapter': 'unreal-engine', 'policy': 'per-user-per-release', 'rootDirectory': str(root/'client/runtime-data')}:
                raise ValueError('Runtime data policy changed')
    for name, digest in f.get('configHashes', {}).items():
        if sha256(inside(root, name)) != digest: raise ValueError('Operational fixture configuration changed')
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(inside(root, 'client/agent')), UE_DT_AGENT_ENDPOINT=f['endpoint'])
    return root, f, env


def release_paths(root, version):
    relative = 'ma0t10-dt/prod/stable/'+version+'/windows-x64'
    return inside(root, 'client/apps/'+relative), inside(root, 'client/state/'+relative)


def require_stopped(root, version):
    app, state = release_paths(root, version)
    record = json.loads(inside(state, 'runtime-state.json').read_text())
    expected_id = hashlib.sha256(str(app).rstrip('\\/').upper().encode()).hexdigest()
    if record.get('schemaVersion') != 1 or record.get('installationId') != expected_id or record.get('state') != 0 or record.get('origin') not in ('supervisor-completed', 'new-install'):
        raise ValueError('Valid supervised Quiescent record required; no guessed stopped state')
    if inside(state, 'transaction.json').exists(): raise ValueError('Unfinished transaction')
    if record['origin'] == 'supervisor-completed':
        if not re.fullmatch(r'[a-f0-9]{32}', record.get('attemptId', '')) or any(not record.get(p) for p in ('host', 'requester', 'entryPoint', 'manifestHash', 'tokenHash')):
            raise ValueError('Incomplete supervised completion record')


@contextmanager
def mutation_lock(root, version):
    # Same Windows sharing semantics as FileStream(FileShare.None), not a PID assertion.
    # Keep the lock handle across stopped recheck and the acceptance-only mutation.
    if os.name != 'nt': raise ValueError('Real UE fixture mutation currently supports Windows only')
    import ctypes
    from ctypes import wintypes
    _, state = release_paths(root, version)
    path = inside(state, 'update.lock')
    if not path.exists(): raise ValueError('No existing installation lock; no fixture initialization bypass')
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE; kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0xc0000000, 0, None, 3, 0x80, None)
    if handle == wintypes.HANDLE(-1).value: raise OSError(ctypes.get_last_error(), 'Installation is busy')
    try: require_stopped(root, version); yield
    finally: kernel.CloseHandle(handle)


def snapshot(root, version):
    app, state = release_paths(root, version)
    manifest = json.loads(inside(state, 'installed-manifest.json').read_text())
    managed = {e['path']: sha256(inside(app, e['path'])) for e in manifest['files']}
    all_app = inventory(app)
    return {'version': version, 'managed': managed,
        'unmanaged': {p: v for p, v in all_app.items() if p not in managed},
        'state': inventory(state), 'data': inventory(inside(root, 'client/runtime-data')) if inside(root, 'client/runtime-data').exists() else {}}


def hold(root, f, env):
    with FixtureHarnessLock(root):
        inside(root, 'control/stop-all').unlink(missing_ok=True)
        logs = []; processes = {}
        sink = http.server.ThreadingHTTPServer(('127.0.0.1', int(f['sinkOrigin'].rsplit(':', 1)[1])), OfflineSink)
        sink.daemon_threads = True
        threading.Thread(target=sink.serve_forever, daemon=True).start()
        try:
            for name in ('server', 'agent'):
                if name == 'agent' and f['mode'] == 'portable': continue
                output = (root/(name+'.log')).open('a', encoding='utf-8'); logs.append(output)
                args = [str(inside(root, f['binaries'][name]['relativePath']))]
                if name == 'server': args += ['serve', '--config', str(root/'server.json')]
                processes[name] = subprocess.Popen(args, env=env, stdout=output, stderr=output, creationflags=FLAGS)
                if name == 'server': wait_server_ready(processes[name], f['origin'])
                else: wait_agent_ready(processes[name], f['endpoint'])
            print('READY: '+str(root)+'; not installed or launched', flush=True)
            while not (root/'control/stop-all').exists():
                if any(p.poll() is not None for p in processes.values()): raise RuntimeError('Owned fixture service exited')
                time.sleep(.2)
        finally:
            sink.shutdown(); sink.server_close()
            for p in reversed(list(processes.values())):
                if p.poll() is None: p.terminate(); p.wait(timeout=15)
            for stream in logs: stream.close()


def prepare(args):
    if os.name != 'nt' or not args.approve: raise ValueError('Windows preparation requires explicit isolated --approve')
    validate_versions(args.versions)
    package, root = validate_package(args.package, args.root)
    if package.is_relative_to(root): raise ValueError('Source must not be inside output')
    inputs = {n: Path(getattr(args, n)).resolve() for n in ('launcher', 'agent', 'server') if n != 'agent' or args.mode == 'managed'}
    for n, p in inputs.items():
        if p.name != COHORT_FILES[n] or not p.is_file() or p.is_symlink(): raise ValueError('Published executable mismatch')
    cohort = validate_cohort(args.cohort, inputs) if args.cohort else None
    if args.runtime_data and cohort is None: raise ValueError('Runtime-data tests require a frozen publication cohort')
    source = inventory(package)
    if not any(p.endswith(('.pak', '.ucas')) and v['size'] > 0 for p, v in source.items()): raise ValueError('Nonempty cooked payload required')
    if any(p.startswith('FixtureAcceptance/') for p in source): raise ValueError('Acceptance files already present in input')
    root.mkdir(parents=True, exist_ok=True)
    f = {'schemaVersion': 2, 'kind': 'same-ue-payload-file-delta', 'projectId': 'ma0t10-dt',
         'versions': args.versions, 'mode': args.mode, 'endpoint': fixture_endpoint(root), 'binaries': {},
         'sourceInventoryHash': fingerprint(source), 'sourcePackage': str(package),
         'ueBuildMeaning': 'same previously built UE payload; only non-executable acceptance text differs',
         'toolHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
         'toolDiffHash': hashlib.sha256(subprocess.check_output(['git', 'diff', 'HEAD'])).hexdigest(),
         'publishSourceHead': args.publish_source_head}
    if cohort is not None:
        if cohort['sourceHead'] != args.publish_source_head: raise ValueError('Publication head mismatch')
        f['publication'] = cohort
    f['runtimeDataEnabled'] = args.runtime_data
    for n, p in inputs.items():
        target = root/('cohort/server' if n == 'server' else 'client/agent' if args.mode == 'managed' else 'client/portable')/p.name
        target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(p, target)
        f['binaries'][n] = {'relativePath': target.relative_to(root).as_posix(), 'sha256': sha256(target)}
        if n == 'server':
            for key, item in copy_server_support(p, target).items():
                f['binaries'][key] = {'relativePath': Path(item['path']).relative_to(root).as_posix(), 'sha256': item['sha256']}
    env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(root/'client/agent'), UE_DT_AGENT_ENDPOINT=f['endpoint'])
    def run(n, *a): return command(root, f, env, n, *a)
    for key in ('origin', 'sinkOrigin'):
        with socket.socket() as s: s.bind(('127.0.0.1', 0)); f[key] = 'http://127.0.0.1:'+str(s.getsockname()[1])
    write(root/'source-inventory.json', source)
    write(root/'policy.json', {'clients': [{'id': 'ue-test-pc', 'addresses': ['127.0.0.1/32'],
        'grants': [{'projectId': 'ma0t10-dt', 'environment': 'prod', 'channel': 'stable', 'versions': args.versions}]}]})
    run('launcher', 'generate-signing-key', '--private-key', root/'release.pem', '--public-key', root/'public.pem')
    write(root/'server.json', {'root': str(root/'server'), 'publicUrl': f['origin'], 'listenUrl': f['origin'],
        'signingKeyPath': str(root/'release.pem'), 'policyPath': str(root/'policy.json'), 'authenticationMode': 'request-signature-v1'})
    run('launcher', 'credential', 'keygen', '--name', 'ue-test-device', '--key-id', 'ue-test-device-key',
        '--public-out', root/'device-public.json', '--storage', 'managed' if args.mode == 'managed' else 'portable')
    run('server', 'client-key', 'add', '--client', 'ue-test-pc', '--public-key', root/'device-public.json', '--config', root/'server.json')
    f['releases'] = []
    for index, version in enumerate(args.versions, 1):
        upload = root/('server/incoming/ue-00'+str(index)); upload.mkdir(parents=True)
        archive = upload/'Windows.zip'; archive_release(package, archive, version, index, source)
        run('launcher', 'release-metadata', '--zip', archive, '--project-id', 'ma0t10-dt', '--display-name', 'MA0T10 DT — 격리된 버전 시험',
            '--version', version, '--platform', 'windows-x64', '--entry-point', 'ma0t10_dt.exe', '--output', upload/'release.json')
        job = json.loads(run('server', 'ingest', upload, '--config', root/'server.json'))
        if job['state'] != 'pending': raise ValueError('Not approval-pending')
        if index == 1: run('server', 'approve', job['id'], '--config', root/'server.json')
        f['releases'].append({'version': version, 'jobId': job['id'], 'zipSha256': sha256(archive), 'initialState': 'published' if index == 1 else 'pending'})
    run('launcher', 'sample-config', '--server-url', f['origin'], '--project-id', 'ma0t10-dt', '--profile', 'developer', '--platform', 'windows-x64',
        '--deployment-mode', 'managed-agent' if args.mode == 'managed' else 'portable', '--credential-name', 'ue-test-device', '--public-key', root/'public.pem', '--output', root/'generated.json')
    config = json.loads((root/'generated.json').read_text(encoding='utf-8-sig'))
    config.update(installDir=str(root/'client/apps'), stateRootDir=str(root/'client/state'), logDir=str(root/'client/logs'),
        versionPolicy='exact', requestedVersion=args.versions[0], launchArguments=[
            '-windowed', '-ResX=1280', '-ResY=720', '-d3d12', f'-UserDir={root}/legacy-user/', f'-abslog={root}/legacy-runtime.log',
            *[f'-ini:Game:[DTCoreRuntimeOverride]:{key}={f["sinkOrigin"]}' for key in ('BaseApiUrl', 'LocalApiUrl', 'TestApiUrl', 'ProdApiUrl')],
            f'-ini:Game:[DTCoreRuntimeOverride]:WebSocketUrl=ws://127.0.0.1:{f["sinkOrigin"].rsplit(":", 1)[1]}',
            '-ini:Game:[/Script/DTCore.DTCoreSettings]:MaxReconnectAttempts=1'])
    if args.runtime_data:
        config['launchArguments'] = [a for a in config['launchArguments'] if not a.startswith(('-UserDir=', '-abslog='))]
        config['runtimeData'] = {'enabled': True, 'adapter': 'unreal-engine', 'policy': 'per-user-per-release', 'rootDirectory': str(root/'client/runtime-data')}
    if args.mode == 'managed': write(root/'client/agent/config/launcher.config.json', config)
    for profile in ('general', 'developer'):
        gui = dict(config, clientProfile=profile)
        if args.mode == 'managed': gui['security'] = dict(config['security'], credentialName='gui-must-not-read-device-key')
        write(root/('client/'+profile+'.json'), gui)
    write(root/'versioned-real-fixture.json', f)
    if args.runtime_data:
        subprocess.run(['pwsh', '-NoProfile', '-File', str(Path(__file__).with_name('provision-real-fixture-data.ps1')), '-Root', str(root)], check=True, creationflags=FLAGS)
    config_names = ['server.json'] + (['client/agent/config/launcher.config.json'] if args.mode == 'managed' else [])
    f['configHashes'] = {name: sha256(inside(root, name)) for name in config_names}
    write(root/'versioned-real-fixture.json', f)
    if (root/'client/apps').exists(): raise ValueError('No CLI preinstallation is permitted')
    hold(root, f, env)


def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--root', required=True)
    sub = parser.add_subparsers(dest='action', required=True)
    p = sub.add_parser('prepare')
    for n in ('package', 'launcher', 'server', 'publish-source-head'): p.add_argument('--'+n, required=True)
    p.add_argument('--agent'); p.add_argument('--mode', choices=['managed', 'portable'], default='managed')
    p.add_argument('--cohort'); p.add_argument('--runtime-data', action='store_true')
    p.add_argument('--versions', nargs=2, default=['0.1.0-ue-test.v1', '0.1.0-ue-test.v2']); p.add_argument('--approve', action='store_true')
    for action in ('resume', 'status', 'open-general', 'open-developer', 'approve-v2', 'stop-services'):
        sub.add_parser(action)
    for action in ('verify', 'snapshot', 'damage', 'verify-backup', 'invalidate-preview', 'compare'):
        p = sub.add_parser(action); p.add_argument('--version', required=True)
        if action in ('snapshot', 'compare'): p.add_argument('--name', required=True)
    args = parser.parse_args()
    if args.action == 'prepare': return prepare(args)
    root, f, env = load(args.root)
    if getattr(args, 'version', None) is not None and args.version not in f['versions']: raise ValueError('Unknown fixture version')
    if args.action == 'resume': return hold(root, f, env)
    if args.action.startswith('open-'):
        profile = args.action.removeprefix('open-')
        p = subprocess.Popen([str(inside(root, f['binaries']['launcher']['relativePath'])), '--gui', '--config', str(root/('client/'+profile+'.json'))], env=env, creationflags=FLAGS)
        print(json.dumps({'guiPid': p.pid, 'profile': profile})); return
    if args.action == 'approve-v2':
        command(root, f, env, 'server', 'approve', f['releases'][1]['jobId'], '--config', root/'server.json'); return
    if args.action == 'status':
        if f['mode'] == 'managed': print(json.dumps(agent_status(f['endpoint'])))
        for version in f['versions']:
            _, state = release_paths(root, version)
            p = inside(state, 'runtime-state.json')
            print(json.dumps({'version': version, 'runtime': json.loads(p.read_text())['state'] if p.exists() else 'not-installed'}))
        return
    if args.action == 'stop-services':
        p = inside(root, 'control/stop-all'); p.parent.mkdir(exist_ok=True); p.touch(); return
    app, state = release_paths(root, args.version)
    if args.action == 'verify': print('PASS: Manifest files='+str(verify_files(app, inside(state, 'installed-manifest.json'))))
    if args.action == 'snapshot':
        if not re.fullmatch(r'[a-zA-Z0-9-]{1,64}', args.name): raise ValueError('Invalid evidence label')
        value = snapshot(root, args.version); path = inside(root, 'evidence/'+args.name+'.json')
        if path.exists(): raise ValueError('Existing snapshot is never overwritten')
        write(path, value)
        print(json.dumps({'managedHash': fingerprint(value['managed']), 'dataHash': fingerprint(value['data']), 'unmanagedFiles': list(value['unmanaged'])}))
    if args.action == 'compare':
        if not re.fullmatch(r'[a-zA-Z0-9-]{1,64}', args.name): raise ValueError('Invalid evidence label')
        previous = json.loads(inside(root, 'evidence/'+args.name+'.json').read_text())
        current = snapshot(root, args.version)
        if previous != current: raise ValueError('Snapshot changed; inspect protected and unmanaged inventories separately')
        print('PASS: unchanged protected/state/data snapshot')
    if args.action == 'damage':
        with mutation_lock(root, args.version):
            manifest = json.loads(inside(state, 'installed-manifest.json').read_text())
            name = 'FixtureAcceptance/changed.txt'
            if name not in {e['path'] for e in manifest['files']}: raise ValueError('Only known installed acceptance text may be damaged')
            inside(app, name).write_text('intentional isolated acceptance damage\n')
    if args.action == 'invalidate-preview':
        with mutation_lock(root, args.version):
            backups = sorted(inside(state, 'backups').glob('[0-9]*'), reverse=True)
            if not backups: raise ValueError('No backup')
            target = inside(backups[0], '.uedt-meta/backup-info.json')
            with target.open('a', encoding='utf-8') as stream: stream.write(' ')
    if args.action == 'verify-backup':
        require_stopped(root, args.version)
        backups = sorted(inside(state, 'backups').glob('[0-9]*'), reverse=True)
        if not backups: raise ValueError('No backup')
        backup = backups[0]
        info = json.loads(inside(backup, '.uedt-meta/backup-info.json').read_text())
        if info['previousVersion'] != args.version or info['newVersion'] != args.version or info['addedPaths']:
            raise ValueError('Not a same-version normal-file backup')
        manifest_path = inside(backup, '.uedt-meta/installed-manifest.json')
        count = verify_files(backup, manifest_path)
        if not count: raise ValueError('Empty backup is not a normal-file recovery proof')
        print(json.dumps({'backupId': backup.name, 'normalManifestFiles': count}))


if __name__ == '__main__': main()
