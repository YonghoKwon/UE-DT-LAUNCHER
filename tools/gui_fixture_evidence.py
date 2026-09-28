"""Read/verify only files inside a synthetic fixture; no UI automation."""
import hashlib, json, os, socket, struct, uuid, base64
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

def verify_cohort(root):
    fixture = json.loads(inside(root, 'fixture.json').read_text())
    for value in fixture['binaries'].values():
        if sha256(value['path']) != value['sha256']: raise ValueError('Fixture binary changed')
    return fixture

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
