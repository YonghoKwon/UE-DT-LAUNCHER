"""Synthetic post-restore filesystem observation. Does not claim an Agent ACK or rollback operation ID."""
import contextlib,ctypes,hashlib,json,os,re,time,uuid
from pathlib import Path
from evidence_contract import atomic
from gui_fixture_evidence import inside,sha256,operation_root

def metadata_fingerprint(root,installation_id,backup,state):
    paths=[backup/'.uedt-meta/backup-info.json',backup/'.uedt-meta/installed-manifest.json',backup/'.uedt-meta/install-state.json',state/'installed-manifest.json']
    hashes=[installation_id,backup.name]+[sha256(inside(root,p.relative_to(root))) if p.is_file() else 'missing' for p in paths]
    return hashlib.sha256('\n'.join(hashes).encode()).hexdigest().upper()

def inventory(root,folder):
    result={}
    if not folder.exists():return result
    for path in folder.rglob('*'):
        path=inside(root,path.relative_to(root))
        if path.is_file():
            if len(result)>=10000:raise ValueError('Too many synthetic inventory files')
            result[str(path.relative_to(folder)).replace('\\','/')]=sha256(path)
    return result

@contextlib.contextmanager
def existing_lock(root,path):
    path=inside(root,path.relative_to(root))
    if not path.is_file():yield False;return
    if os.name=='nt':
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
        kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        handle=kernel.CreateFileW(str(path),0xC0000000,0,None,3,0,None)
        if handle==ctypes.c_void_p(-1).value:yield False;return
        try:yield True
        finally:kernel.CloseHandle(handle)
    else:
        import fcntl
        with path.open('r+b') as stream:
            try:fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
            except OSError:yield False;return
            try:yield True
            finally:fcntl.flock(stream.fileno(),fcntl.LOCK_UN)

def paths(root,selection):
    release='/'.join(selection[k] for k in ('projectId','environment','channel','version','platform'))
    for segment in release.split('/'):
        if not re.fullmatch('[A-Za-z0-9][A-Za-z0-9._-]*',segment):raise ValueError('Unsafe witness selection')
    return inside(root,'client/apps/'+release),inside(root,'client/state/'+release)

def latest_backup(root,state):
    entries=sorted([p for p in (state/'backups').iterdir() if p.is_dir() and re.fullmatch('[0-9]{14}(?:-[0-9]{2})?',p.name)],reverse=True)
    if not entries:raise ValueError('No synthetic backup')
    return inside(root,entries[0].relative_to(root))

def arm(root,fixture,version):
    from gui_fixture_safety import mutation_guard
    if not fixture.get('productPublication'):raise ValueError('A final snapshot fixture is required')
    selection={'projectId':'demo','environment':'prod','channel':'stable','version':version,'platform':fixture['platform']}
    app,state=paths(root,selection);control=inside(root,'control');pending=control/'proxy-fail-catalog-after-restore'
    if pending.exists():raise ValueError('A restore witness is already armed')
    with mutation_guard(root,fixture,version):
        backup=latest_backup(root,state);meta=backup/'.uedt-meta';manifest=meta/'installed-manifest.json'
        if not manifest.is_file():raise ValueError('An installed-state normal backup is required')
        document=json.loads(manifest.read_text(encoding='utf-8-sig'))
        expected=inventory(root,backup);expected={name:digest for name,digest in expected.items() if not name.startswith('.uedt-meta/')}
        for entry in document['files']:
            if expected.get(entry['path'])!=entry['sha256'].lower():raise ValueError('Backup contents are not normal')
        before=inventory(root,app)
        if before==expected:raise ValueError('Arm requires a real damaged pre-restore payload')
        runtime=state/'runtime-state.json';record=json.loads(runtime.read_text())
        metadata={name:sha256(meta/name) if (meta/name).is_file() else None for name in ('installed-manifest.json','install-state.json','backup-info.json')}
        witness={'schemaVersion':1,'kind':'restore-file-transition','attemptId':uuid.uuid4().hex,'fixtureId':fixture['id'],
            'productSourceHash':fixture['productPublication']['productSourceHash'],'selection':selection,'installationId':record['installationId'],
            'runtimeSha256':sha256(runtime),'backupId':backup.name,'metadataFingerprint':metadata_fingerprint(root,record['installationId'],backup,state),
            'backupMetadata':metadata,'expectedPayload':expected,'beforePayload':before,
            'operationsRelative':str(operation_root(root,fixture).relative_to(root)),
            'beforeOperationIds':[p.stem for p in operation_root(root,fixture).glob('*.json')]}
        atomic(pending,witness)
    return {k:witness[k] for k in ('kind','attemptId','backupId','metadataFingerprint','installationId')}

def observe(root,fixture):
    control=inside(root,'control');pending=control/'proxy-fail-catalog-after-restore'
    if not pending.is_file():return None
    if pending.stat().st_size>2*1024*1024:raise ValueError('Oversized restore witness')
    value=json.loads(pending.read_text())
    if value.get('schemaVersion')!=1 or value.get('kind')!='restore-file-transition' or value.get('fixtureId')!=fixture['id'] or value.get('productSourceHash')!=fixture.get('productPublication',{}).get('productSourceHash'):return None
    if not re.fullmatch('[a-f0-9]{32}',value.get('attemptId','')):return None
    fired=control/'restore-witnesses'/(value['attemptId']+'.json')
    if fired.exists():return None
    app,state=paths(root,value['selection'])
    try:backup=latest_backup(root,state)
    except (OSError,ValueError):return None
    if backup.name!=value['backupId']:return None
    runtime=state/'runtime-state.json'
    if not runtime.is_file() or sha256(runtime)!=value['runtimeSha256']:return None
    record=json.loads(runtime.read_text())
    if record.get('installationId')!=value['installationId'] or record.get('state')!=0:return None
    if {p.stem for p in inside(root,value['operationsRelative']).glob('*.json')}-set(value['beforeOperationIds']):return None
    with existing_lock(root,state/'update.lock') as available:
        if not available or (state/'transaction.json').exists():return None
        for name,digest in value['backupMetadata'].items():
            path=backup/'.uedt-meta'/name
            if (sha256(path) if path.is_file() else None)!=digest:return None
        if inventory(root,app)!=value['expectedPayload']:return None
        for name in ('installed-manifest.json','install-state.json'):
            path=state/name
            if (sha256(path) if path.is_file() else None)!=value['backupMetadata'][name]:return None
        observed={'kind':'restore-file-transition','attemptId':value['attemptId'],'backupId':value['backupId'],
            'installationId':value['installationId'],'metadataFingerprint':value['metadataFingerprint'],'files':len(value['expectedPayload'])}
        atomic(fired,observed);pending.unlink();return observed
