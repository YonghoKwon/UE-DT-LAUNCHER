"""Read/verify only files inside a synthetic fixture; no UI automation."""
import hashlib, json, os, socket, struct, uuid, base64, subprocess, time, urllib.request, urllib.error, urllib.parse, shutil
from pathlib import Path

def inside(root, relative):
    root = Path(root).resolve()
    candidate = root / relative
    current = candidate
    while current != root:
        if current.is_symlink() or (hasattr(current, 'is_junction') and current.is_junction()):
            raise ValueError('Linked fixture paths are not supported')
        if current.parent == current: raise ValueError('Path outside fixture')
        current = current.parent
    resolved = candidate.resolve()
    if not resolved.is_relative_to(root): raise ValueError('Path outside fixture')
    return resolved

def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def verify_files(folder, manifest):
    files = json.loads(Path(manifest).read_text(encoding='utf-8-sig'))['files']
    for entry in files:
        path = inside(folder, entry['path'])
        if not path.is_file() or path.stat().st_size != entry['size'] or sha256(path).lower() != entry['sha256'].lower():
            raise ValueError('Fixture file does not match manifest: '+entry['path'])
    return len(files)

def copy_server_support(source_executable, destination_executable):
    """Only known single-file publish runtime sidecars; never copy configs, keys or logs."""
    source=Path(source_executable).resolve();destination=Path(destination_executable).resolve()
    allowed=('e_sqlite3.dll','libe_sqlite3.so','libe_sqlite3.dylib',source.stem+'.deps.json',source.stem+'.runtimeconfig.json')
    copied={}
    for name in allowed:
        sidecar=source.parent/name
        if not sidecar.exists():continue
        if sidecar.is_symlink() or not sidecar.is_file():raise ValueError('Linked or non-file runtime support is not allowed')
        target=destination.parent/name
        shutil.copy2(sidecar,target)
        copied['server/'+name]={'path':str(target),'sha256':sha256(target)}
    return copied

def verify_cohort(root):
    fixture = json.loads(inside(root, 'fixture.json').read_text())
    if fixture.get('schemaVersion') not in (1, 2): raise ValueError('Unsupported GUI fixture schema')
    if fixture.get('schemaVersion') == 2:
        if fixture.get('deploymentMode') not in ('managed', 'portable'): raise ValueError('Invalid GUI fixture mode')
        required = {'launcher', 'server', 'synthetic'} | ({'agent'} if fixture['deploymentMode'] == 'managed' else set())
        if set(fixture['binaries']) != required: raise ValueError('Wrong binary set for fixture mode')
    for value in fixture['binaries'].values():
        if fixture.get('schemaVersion') == 2: inside(root, Path(value['path']).relative_to(Path(root).resolve()))
        if sha256(value['path']) != value['sha256']: raise ValueError('Fixture binary changed')
    for value in fixture.get('supportFiles',{}).values():
        inside(root,Path(value['path']).relative_to(Path(root).resolve()))
        if sha256(value['path'])!=value['sha256']:raise ValueError('Fixture runtime support changed')
    settings=json.loads(inside(root,'server.json').read_text())
    validate_origin(settings['publicUrl']);validate_origin(settings['listenUrl'])
    for field in ('root','policyPath','signingKeyPath'):
        inside(root,Path(settings[field]).relative_to(Path(root).resolve()))
    return fixture

class FixtureHarnessLock:
    """Process-lifetime ownership, not a PID assertion. Never remove the lock inode."""
    def __init__(self,root): self.path=inside(root,'control/harness.lock');self.stream=None
    def __enter__(self):
        self.path.parent.mkdir(exist_ok=True)
        stream=self.path.open('a+b')
        try:
            if stream.seek(0,2)==0:stream.write(b'\0');stream.flush()
            stream.seek(0)
            if os.name=='nt':
                import msvcrt
                msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
        except OSError:
            stream.close();raise RuntimeError('A harness already owns this fixture')
        self.stream=stream;return self
    def close(self):
        if self.stream is not None:
            self.stream.close();self.stream=None
    def __exit__(self,*arguments):self.close()

def validate_origin(origin):
    value=urllib.parse.urlsplit(origin)
    if value.scheme!='http' or value.hostname!='127.0.0.1' or value.username or value.password or value.path not in ('','/') or value.query or value.fragment or not value.port:
        raise ValueError('Fixture server must be a loopback HTTP origin')
    return origin.rstrip('/')

def fixture_mode(fixture):
    return fixture.get('deploymentMode', 'managed')

def fixture_environment(root, fixture):
    values = json.loads(inside(root, 'test-environment.json').read_text())
    if set(values) != {'UE_DT_AGENT_DATA_ROOT', 'UE_DT_AGENT_ENDPOINT'}: raise ValueError('Unexpected fixture environment')
    inside(root, Path(values['UE_DT_AGENT_DATA_ROOT']).relative_to(Path(root).resolve()))
    return dict(os.environ, **values)

def wait_server_ready(process, origin, timeout=30):
    origin=validate_origin(origin)
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        if process.poll() is not None: raise RuntimeError('Fixture server exited before ready')
        try: urllib.request.urlopen(origin+'/api/v1/catalog', timeout=1).close()
        except urllib.error.HTTPError as error:
            if error.code == 401: return
        except (urllib.error.URLError, TimeoutError, OSError): pass
        time.sleep(.1)
    raise TimeoutError('Fixture server did not become ready')

def wait_agent_ready(process, endpoint, timeout=30):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        if process.poll() is not None: raise RuntimeError('Fixture Agent exited before ready')
        try:
            value=agent_status(endpoint)
            if value.get('success') and {'runtime-supervision-v1','rollback-preview-v1'} <= set(value.get('agentCapabilities', [])): return value
        except (OSError, RuntimeError, ValueError): pass
        time.sleep(.1)
    raise TimeoutError('Fixture Agent did not become ready')

def record_control(root, action, **details):
    control=inside(root,'control'); control.mkdir(exist_ok=True)
    with (control/'events.jsonl').open('a',encoding='utf-8') as stream:
        stream.write(json.dumps(dict(atUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),action=action,**details))+'\n')

def hold_fixture(root, fixture, env, server_process, agent_process, server_log, agent_log):
    """Own only our Popen handles. Intentional server downtime does not end the harness."""
    control=inside(root,'control'); control.mkdir(exist_ok=True)
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    origin=json.loads(inside(root,'server.json').read_text())['publicUrl']
    processes={'server':server_process,'agent':agent_process}
    expected_stopped={'server':False,'agent':fixture_mode(fixture)=='portable'}
    try:
        while not (control/'stop-all').exists():
            for kind in ('server','agent'):
                if kind=='agent' and fixture_mode(fixture)=='portable': continue
                process=processes[kind]
                stop=control/('stop-'+kind); start=control/('start-'+kind)
                if stop.exists():
                    if process is not None and process.poll() is None:
                        process.terminate(); process.wait(timeout=10)
                    expected_stopped[kind]=True; stop.unlink()
                    (control/(kind+'-stopped')).touch(); record_control(root,kind+'-stopped')
                if start.exists():
                    verify_cohort(root)
                    if process is None or process.poll() is not None:
                        command=[fixture['binaries'][kind]['path']]
                        if kind=='server': command += ['serve','--config',str(inside(root,'server.json'))]
                        output=server_log if kind=='server' else agent_log
                        process=subprocess.Popen(command,env=env,stdout=output,stderr=output,creationflags=flags)
                        processes[kind]=process
                    if kind=='server': wait_server_ready(process,origin)
                    else: wait_agent_ready(process,env['UE_DT_AGENT_ENDPOINT'])
                    expected_stopped[kind]=False; start.unlink()
                    (control/(kind+'-stopped')).unlink(missing_ok=True)
                    (control/(kind+'-ready')).touch(); record_control(root,kind+'-ready')
                if not expected_stopped[kind] and process is not None and process.poll() is not None:
                    raise RuntimeError('Fixture '+kind+' exited unexpectedly')
            time.sleep(.1)
    finally:
        for process in processes.values():
            if process is not None and process.poll() is None:
                process.terminate(); process.wait(timeout=10)

def agent_status(endpoint):
    channel = None
    if os.name == 'nt':
        connection = open('\\\\.\\pipe\\'+endpoint, 'r+b', buffering=0)
    else:
        channel=socket.socket(socket.AF_UNIX); channel.settimeout(5); channel.connect(endpoint)
        connection=channel.makefile('rwb',buffering=0)
    try:
        request=dict(protocolVersion=1,correlationId=uuid.uuid4().hex,command='status')
        body=json.dumps(request).encode(); connection.write(struct.pack('<i',len(body))+body)
        def read_exact(size):
            output=bytearray()
            while len(output)<size:
                chunk=connection.read(size-len(output))
                if not chunk: raise OSError('Incomplete Agent frame')
                output.extend(chunk)
            return output
        size=struct.unpack('<i',read_exact(4))[0]
        if not 0<size<=1024*1024: raise ValueError('Invalid Agent frame')
        response=json.loads(read_exact(size))
        if response.get('correlationId')!=request['correlationId']: raise ValueError('Wrong correlation')
        return response
    finally:
        connection.close()
        if channel: channel.close()

def preference_path():
    base=Path(os.environ['LOCALAPPDATA']) if os.name=='nt' else Path(os.environ.get('XDG_CONFIG_HOME',str(Path.home()/'.config')))
    return base/'UE-DT Launcher'/'ui-preferences.json'

def snapshot_preferences(root):
    path=preference_path()
    if path.is_symlink(): raise ValueError('Linked preferences are not supported')
    original=path.read_bytes() if path.exists() else None
    if original is not None and len(original)>4096: raise ValueError('Unexpected preferences size')
    inside(root,'ui-preferences-original.json').write_text(json.dumps({'original':base64.b64encode(original).decode() if original is not None else None}))

def preference_hash():
    path=preference_path()
    return sha256(path) if path.exists() else 'absent'

def restore_preferences(root, expected):
    if preference_hash()!=expected: raise ValueError('Preferences changed since the last owned write; refusing overwrite')
    path=preference_path()
    if path.is_symlink(): raise ValueError('Linked preferences are not supported')
    data=json.loads(inside(root,'ui-preferences-original.json').read_text())['original']
    if data is None: path.unlink(missing_ok=True)
    else:
        path.parent.mkdir(parents=True,exist_ok=True)
        temporary=path.with_name('fixture-'+uuid.uuid4().hex+'.tmp')
        temporary.write_bytes(base64.b64decode(data));temporary.replace(path)
