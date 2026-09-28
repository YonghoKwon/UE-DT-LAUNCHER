"""Controls only a prepared synthetic GUI fixture. Never operates on company installations."""
import argparse, hashlib, json, os, re, subprocess, time, zipfile
from pathlib import Path
from gui_fixture_evidence import inside, verify_files, verify_cohort, preference_hash, restore_preferences, snapshot_preferences
p=argparse.ArgumentParser(); p.add_argument('--root',required=True)
p.add_argument('action',choices=['smoke','gui-general','gui-developer','snapshot','compare','damage','release','stop-agent','start-agent','stop-all','status','approve-v2','verify','verify-backup','invalidate-preview','prefs-snapshot','prefs-record-owned','prefs-restore'])
p.add_argument('--version',choices=['1.0.0','2.0.0'],default='1.0.0'); p.add_argument('--name',default='before')
p.add_argument('--attempt', help='Exact synthetic child marker id to release')
a=p.parse_args(); root=Path(a.root).resolve()
assert (root/'summary.json').exists() and json.loads((root/'summary.json').read_text())['gui_prepared_empty']
fixture=verify_cohort(root)
control=root/'control'; control.mkdir(exist_ok=True)
env=dict(os.environ,**json.loads((root/'test-environment.json').read_text()))
exe=root/'client'/'agent'/'UeDtLauncher.exe'
platform='windows-x64' if os.name=='nt' else 'linux-x64'
state=inside(root,'client/state/demo/prod/stable/'+a.version+'/'+platform)
app=inside(root,'client/apps/demo/prod/stable/'+a.version+'/'+platform)
def latest_backup():
    candidates=sorted((state/'backups').iterdir(),reverse=True)
    return next(inside(root,str(path.relative_to(root))) for path in candidates if path.is_dir())
def snapshot():
    roots=[root/'client'/'apps'/'demo'/'prod'/'stable'/a.version,root/'client'/'state'/'demo'/'prod'/'stable'/a.version]
    return {str(f.relative_to(root)):hashlib.sha256(f.read_bytes()).hexdigest() for folder in roots if folder.exists()
            for f in folder.rglob('*') if f.is_file() and not f.name.endswith('.lock')}
if a.action=='prefs-snapshot':
    if (root/'ui-preferences-original.json').exists(): raise ValueError('Original preferences already captured')
    snapshot_preferences(root); print('Original preferences captured')
elif a.action=='prefs-record-owned':
    (control/'ui-preferences-owned.sha256').write_text(preference_hash())
    print('Recorded last explicitly test-owned preference value')
elif a.action=='prefs-restore':
    restore_preferences(root,(control/'ui-preferences-owned.sha256').read_text())
    print('Restored original preferences after hash guard')
elif a.action=='approve-v2':
    job=fixture['pendingJobs'].get('2.0.0')
    if not job: raise ValueError('No pending v2 in this fixture')
    subprocess.run([fixture['binaries']['server']['path'],'approve',job,'--config',str(root/'server.json')],env=env,check=True,capture_output=True)
    print('Approved fixture v2')
elif a.action=='verify':
    print('PASS: installed files='+str(verify_files(app,state/'installed-manifest.json')))
elif a.action=='verify-backup':
    backup=latest_backup(); meta=backup/'.uedt-meta'
    info=json.loads((meta/'backup-info.json').read_text())
    if info.get('previousVersion')!=a.version or info.get('addedPaths'): raise ValueError('Not a normal same-version backup')
    print('PASS: backup='+backup.name+' files='+str(verify_files(backup,meta/'installed-manifest.json')))
elif a.action=='invalidate-preview':
    target=latest_backup()/'.uedt-meta'/'backup-info.json'
    with target.open('a',encoding='utf-8') as stream: stream.write(' ')
    print('Changed fixture preview fingerprint only')
elif a.action.startswith('gui-'):
    profile=a.action[4:]
    process=subprocess.Popen([str(exe),'--gui','--config',str(root/'client'/(profile+'.json'))],env=env)
    print(json.dumps(dict(pid=process.pid,profile=profile)))
elif a.action in ('snapshot','compare'):
    assert re.fullmatch(r'[a-zA-Z0-9-]+',a.name)
    path=control/(a.name+'.snapshot.json')
    if a.action=='snapshot': path.write_text(json.dumps(snapshot(),indent=2)); print('SNAPSHOT '+str(path))
    else: assert json.loads(path.read_text())==snapshot(),'Protected file set/hash changed'; print('PASS: protected snapshot unchanged')
elif a.action=='damage':
    target=root/'client'/'apps'/'demo'/'prod'/'stable'/a.version/'windows-x64'/'version.txt'
    target=inside(root,str(target.relative_to(root)))
    assert target.is_file(); target.write_text('fixture-damaged'); print('Damaged synthetic version.txt only')
elif a.action=='release':
    if not a.attempt or not re.fullmatch(r'[a-f0-9]{32}',a.attempt): raise ValueError('An exact --attempt is required')
    marker=inside(control,a.attempt+'.started.json')
    if not marker.exists(): raise ValueError('Unknown synthetic attempt')
    inside(control,'release-'+a.attempt).touch()
    deadline=time.monotonic()+15
    while not inside(control,a.attempt+'.ended.json').exists():
        if time.monotonic()>deadline: raise TimeoutError('Synthetic natural exit not confirmed')
        time.sleep(.1)
    print('Confirmed natural child exit')
elif a.action=='status':
    for f in sorted(control.glob('*.started.json')): print(f.read_text()+' ended='+str(f.with_name(f.name.replace('.started.','.ended.')).exists()))
    for f in (root/'client'/'state').rglob('runtime-state.json'):
        value=json.loads(f.read_text()); print(json.dumps(dict(path=str(f.relative_to(root)),state=value['state'],attempt=value.get('attemptId'))))
elif a.action=='smoke':
    folder=root/'smoke'; folder.mkdir()
    with zipfile.ZipFile(root/'server'/'incoming'/'1.0.0'/'Package.zip') as package:
        for name in ('game.exe','version.txt'): (folder/name).write_bytes(package.read(name))
    process=subprocess.Popen([str(folder/'game.exe'),'--control-root',str(control)],creationflags=subprocess.CREATE_NO_WINDOW)
    assert process.wait(timeout=10)==0
    deadline=time.monotonic()+10
    while not list(control.glob('*.started.json')) and time.monotonic()<deadline: time.sleep(.05)
    marker=next(control.glob('*.started.json')); value=json.loads(marker.read_text()); assert value['version']=='1.0.0'
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
    (control/a.action).touch(); print(a.action)
