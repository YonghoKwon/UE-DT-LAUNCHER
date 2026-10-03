"""Controls only a prepared synthetic GUI fixture. Never operates on company installations."""
import argparse, hashlib, json, os, re, subprocess, time, zipfile
from pathlib import Path
from promotion_fixture_support import promote
from gui_fixture_evidence import inside, verify_files, verify_cohort, preference_hash, restore_preferences, snapshot_preferences, fixture_mode, fixture_environment, record_control, agent_status, sha256
p=argparse.ArgumentParser(); p.add_argument('--root',required=True)
p.add_argument('action',choices=['smoke','gui-general','gui-developer','snapshot','compare','damage','release','stop-agent','start-agent','stop-server','start-server','stop-all','status','approve-v2','promote-v1','diagnostics','verify','verify-backup','invalidate-preview','prefs-snapshot','prefs-record-owned','prefs-restore','error-401','error-403','error-empty','error-config','config-reset','policy-reset','error-trust','trust-reset'])
p.add_argument('--version',choices=['1.0.0','2.0.0'],default='1.0.0'); p.add_argument('--name',default='before')
p.add_argument('--attempt', help='Exact synthetic child marker id to release')
a=p.parse_args(); root=Path(a.root).resolve()
assert (root/'summary.json').exists() and json.loads((root/'summary.json').read_text())['gui_prepared_empty']
fixture=verify_cohort(root)
control=root/'control'; control.mkdir(exist_ok=True)
env=fixture_environment(root,fixture)
mode=fixture_mode(fixture)
exe=Path(fixture['binaries']['launcher']['path'])
platform=fixture.get('platform','windows-x64' if os.name=='nt' else 'linux-x64')
state=inside(root,'client/state/demo/prod/stable/'+a.version+'/'+platform)
app=inside(root,'client/apps/demo/prod/stable/'+a.version+'/'+platform)
def latest_backup():
    folder=inside(root,str((state/'backups').relative_to(root)))
    candidates=sorted(folder.iterdir(),reverse=True) if folder.exists() else []
    for path in candidates:
        if re.fullmatch(r'[0-9]{14}(?:-[0-9]{2})?',path.name) and path.is_dir():
            return inside(root,str(path.relative_to(root)))
    raise ValueError('No fixture backup exists')
def snapshot():
    roots=[app,state]
    return {str(f.relative_to(root)):hashlib.sha256(inside(root,str(f.relative_to(root))).read_bytes()).hexdigest() for folder in roots if folder.exists()
            for f in folder.rglob('*') if f.is_file() and not f.name.endswith('.lock')}

def atomic_bytes(path,data):
    path=inside(root,str(path.relative_to(root)))
    temporary=path.with_name(path.name+'.fixture-tmp')
    if temporary.exists(): raise ValueError('Prior fixture write is incomplete')
    temporary.write_bytes(data);temporary.replace(path)

def require_stopped():
    runtime=state/'runtime-state.json'
    if runtime.exists() and json.loads(runtime.read_text())['state']!=0:
        raise ValueError('Fixture mutation requires a quiescent selected runtime')

if a.action in ('stop-agent','start-agent') and mode=='portable':
    raise ValueError('Portable fixture has no Agent process or IPC')
if a.action=='prefs-snapshot':
    if (root/'ui-preferences-original.json').exists(): raise ValueError('Original preferences already captured')
    snapshot_preferences(root); print('Original preferences captured')
elif a.action=='prefs-record-owned':
    (control/'ui-preferences-owned.sha256').write_text(preference_hash())
    print('Recorded last explicitly test-owned preference value')
elif a.action=='prefs-restore':
    restore_preferences(root,(control/'ui-preferences-owned.sha256').read_text())
    print('Restored original preferences after hash guard')
elif a.action=='promote-v1':
    def promotion_run(*values):
        return subprocess.check_output([fixture['binaries']['server']['path'],*map(str,values)],env=env,text=True,encoding='utf-8')
    promote(promotion_run,root/'server.json','demo','1.0.0',platform)
    record_control(root,a.action)
    print('Promoted fixture v1; no client install/run performed')
elif a.action=='approve-v2':
    job=fixture['pendingJobs'].get('2.0.0')
    if not job: raise ValueError('No pending v2 in this fixture')
    subprocess.run([fixture['binaries']['server']['path'],'approve',job,'--config',str(root/'server.json')],env=env,check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    def promotion_run(*values):
        return subprocess.check_output([fixture['binaries']['server']['path'],*map(str,values)],env=env,text=True,encoding='utf-8')
    promote(promotion_run,root/'server.json','demo','2.0.0','windows-x64')
    record_control(root,'approve-v2',jobId=job)
    print('Approved fixture v2')
elif a.action=='error-config':
    path=root/'client/general.json';original=control/'original-general.json'
    if original.exists(): raise ValueError('Configuration failure is already prepared')
    original.write_bytes(path.read_bytes())
    atomic_bytes(path,b'{"privateKeyPem":"GUI-SENTINEL",BROKEN')
    record_control(root,a.action);print('Changed only the fixture display configuration')
elif a.action=='config-reset':
    atomic_bytes(root/'client/general.json',(control/'original-general.json').read_bytes())
    record_control(root,a.action);print('Restored fixture display configuration')
elif a.action=='error-401':
    subprocess.run([fixture['binaries']['server']['path'],'client-key','revoke','--key-id','pc-test-key','--config',str(inside(root,'server.json'))],env=env,check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    (control/'auth-key-revoked').touch();record_control(root,a.action)
    print('Revoked this fixture key; irreversible. Use a fresh fixture for successful authentication.')
elif a.action in ('error-403','error-empty','policy-reset'):
    value=json.loads(inside(root,'control/original-policy.json').read_text())
    if a.action=='error-403': value['clients'][0]['addresses']=['192.0.2.0/24']
    if a.action=='error-empty': value['clients'][0]['grants']=[]
    atomic_bytes(root/'policy.json',json.dumps(value,indent=2).encode())
    record_control(root,a.action);print('Changed only the fixture access policy')
elif a.action in ('error-trust','trust-reset'):
    data=inside(root,'control/original-public.pem').read_bytes() if a.action=='trust-reset' else json.loads(inside(root,'device-public.json').read_text())['publicKeyPem'].encode()
    atomic_bytes(root/'public.pem',data)
    record_control(root,a.action);print('Changed only this fixture release verification public key')
elif a.action=='diagnostics':
    output=control/('diagnostics-'+a.name+'.zip')
    if output.exists(): raise ValueError('Use a new diagnostic artifact name')
    subprocess.run([str(exe),'diagnostics','export','--config',str(root/'client/general.json'),'--output',str(output)],env=env,check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    with zipfile.ZipFile(output) as archive:
        report=json.loads(archive.read('doctor.json'))
        if not report.get('supportId') or not report.get('preparationState'): raise ValueError('Missing structured readiness report')
        for item in archive.infolist():
            content=archive.read(item).decode('utf-8')
            if 'PRIVATE KEY' in content or re.search(r'Bearer\s+(?!<redacted>)\S+',content): raise ValueError('Support artifact contains a secret')
        if mode=='managed' and any(item.filename.startswith('state/') for item in archive.infolist()): raise ValueError('Managed client exported protected state')
    record_control(root,a.action,mode=mode)
    print('PASS: structured support ZIP, private-key/Bearer scan and managed state exclusion')
elif a.action=='verify':
    print('PASS: installed files='+str(verify_files(app,state/'installed-manifest.json')))
elif a.action=='verify-backup':
    backup=latest_backup(); meta=backup/'.uedt-meta'
    info=json.loads((meta/'backup-info.json').read_text())
    if info.get('previousVersion')!=a.version or info.get('newVersion')!=a.version or info.get('addedPaths'): raise ValueError('Not a normal same-version backup')
    print('PASS: backup='+backup.name+' files='+str(verify_files(backup,meta/'installed-manifest.json')))
elif a.action=='invalidate-preview':
    require_stopped()
    target=latest_backup()/'.uedt-meta'/'backup-info.json'
    with target.open('a',encoding='utf-8') as stream: stream.write(' ')
    record_control(root,a.action,version=a.version,backupId=target.parent.parent.name)
    print('Changed fixture preview fingerprint only')
elif a.action.startswith('gui-'):
    profile=a.action[4:]
    selected=fixture['binaries'].get('developer',fixture['binaries']['launcher']) if profile=='developer' else fixture['binaries']['launcher']
    process=subprocess.Popen([selected['path'],'--gui','--config',str(root/'client'/(profile+'.json'))],env=env)
    print(json.dumps(dict(pid=process.pid,profile=profile)))
elif a.action in ('snapshot','compare'):
    assert re.fullmatch(r'[a-zA-Z0-9-]+',a.name)
    path=control/(a.name+'.snapshot.json')
    if a.action=='snapshot': path.write_text(json.dumps(snapshot(),indent=2)); print('SNAPSHOT '+str(path))
    else: assert json.loads(path.read_text())==snapshot(),'Protected file set/hash changed'; print('PASS: protected snapshot unchanged')
elif a.action=='damage':
    require_stopped()
    target=app/'version.txt'
    target=inside(root,str(target.relative_to(root)))
    assert target.is_file(); target.write_text('fixture-damaged');record_control(root,a.action,version=a.version);print('Damaged synthetic version.txt only')
elif a.action=='release':
    if not a.attempt or not re.fullmatch(r'[a-f0-9]{32}',a.attempt): raise ValueError('An exact --attempt is required')
    marker=inside(control,a.attempt+'.started.json')
    if not marker.exists(): raise ValueError('Unknown synthetic attempt')
    inside(control,'release-'+a.attempt).touch()
    deadline=time.monotonic()+15
    while not inside(control,a.attempt+'.ended.json').exists():
        if time.monotonic()>deadline: raise TimeoutError('Synthetic natural exit not confirmed')
        time.sleep(.1)
    record_control(root,a.action,attempt=a.attempt)
    print('Confirmed natural child exit')
elif a.action=='status':
    print(json.dumps(dict(deploymentMode=mode,sourceHead=fixture.get('sourceHead'),revoked=(control/'auth-key-revoked').exists())))
    if mode=='managed':
        try:
            response=agent_status(env['UE_DT_AGENT_ENDPOINT'])
            print(json.dumps(dict(agentReady=response.get('success'),capabilities=response.get('agentCapabilities',[]))))
        except OSError: print('Agent not connected')
    for f in sorted(control.glob('*.started.json')): print(f.read_text()+' ended='+str(f.with_name(f.name.replace('.started.','.ended.')).exists()))
    for f in (root/'client'/'state').rglob('runtime-state.json'):
        value=json.loads(f.read_text()); print(json.dumps(dict(path=str(f.relative_to(root)),state=value['state'],attempt=value.get('attemptId'))))
elif a.action=='smoke':
    folder=root/'smoke'; folder.mkdir()
    entry='game.exe' if os.name=='nt' else 'game.sh'
    before=set(control.glob('*.started.json'))
    with zipfile.ZipFile(root/'server'/'incoming'/'1.0.0'/'Package.zip') as package:
        for name in (entry,'version.txt'): (folder/name).write_bytes(package.read(name))
    if sha256(folder/entry)!=fixture['binaries']['synthetic']['sha256'] or (folder/'version.txt').read_text().strip()!='1.0.0':
        raise ValueError('Smoke payload no longer matches the recorded synthetic binary')
    if os.name!='nt': (folder/entry).chmod(0o700)
    process=subprocess.Popen([str(folder/entry),'--control-root',str(control)],creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    assert process.wait(timeout=10)==0
    deadline=time.monotonic()+10
    while not (set(control.glob('*.started.json'))-before) and time.monotonic()<deadline: time.sleep(.05)
    marker=next(iter(set(control.glob('*.started.json'))-before)); value=json.loads(marker.read_text()); assert value['version']=='1.0.0'
    assert not marker.with_name(marker.name.replace('.started.','.ended.')).exists()
    (control/('release-'+value['id'])).touch()
    deadline=time.monotonic()+10
    while not marker.with_name(marker.name.replace('.started.','.ended.')).exists() and time.monotonic()<deadline: time.sleep(.05)
    assert marker.with_name(marker.name.replace('.started.','.ended.')).exists()
    assert not (root/'client'/'apps').exists(), 'GUI fixture must stay uninstalled'
    print('PASS: parent exits first, child persists and exits naturally; GUI install remains empty')
else:
    if a.action=='stop-all':
        for marker in control.glob('*.started.json'): (control/('release-'+marker.name.split('.')[0])).touch()
    acknowledgement=None
    if a.action.startswith(('stop-','start-')) and a.action!='stop-all':
        verb,kind=a.action.split('-');acknowledgement=control/(kind+('-stopped' if verb=='stop' else '-ready'))
        acknowledgement.unlink(missing_ok=True)
    (control/a.action).touch()
    if acknowledgement is not None:
        deadline=time.monotonic()+35
        while not acknowledgement.exists():
            if time.monotonic()>deadline: raise TimeoutError('Fixture harness did not acknowledge '+a.action)
            time.sleep(.1)
    print(a.action)
