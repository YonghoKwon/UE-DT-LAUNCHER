"""Controls only a prepared synthetic GUI fixture. Never operates on company installations."""
import argparse, hashlib, json, os, re, subprocess, time, zipfile
from pathlib import Path
p=argparse.ArgumentParser(); p.add_argument('--root',required=True)
p.add_argument('action',choices=['smoke','gui-general','gui-developer','snapshot','compare','damage','release','stop-agent','start-agent','stop-all','status'])
p.add_argument('--version',choices=['1.0.0','2.0.0'],default='1.0.0'); p.add_argument('--name',default='before')
a=p.parse_args(); root=Path(a.root).resolve()
assert (root/'summary.json').exists() and json.loads((root/'summary.json').read_text())['gui_prepared_empty']
control=root/'control'; control.mkdir(exist_ok=True)
env=dict(os.environ,**json.loads((root/'test-environment.json').read_text()))
exe=root/'client'/'agent'/'UeDtLauncher.exe'
def snapshot():
    roots=[root/'client'/'apps'/'demo'/'prod'/'stable'/a.version,root/'client'/'state'/'demo'/'prod'/'stable'/a.version]
    return {str(f.relative_to(root)):hashlib.sha256(f.read_bytes()).hexdigest() for folder in roots if folder.exists()
            for f in folder.rglob('*') if f.is_file() and not f.name.endswith('.lock')}
if a.action.startswith('gui-'):
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
    assert target.is_file(); target.write_text('fixture-damaged'); print('Damaged synthetic version.txt only')
elif a.action=='release':
    for marker in control.glob('*.started.json'):
        (control/('release-'+marker.name.split('.')[0])).touch()
    print('Requested natural child exit')
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
