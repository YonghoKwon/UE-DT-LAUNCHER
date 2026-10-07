"""Published migration refusal and stopped import proof; no shared ownership is adopted."""
import argparse, hashlib, json, os, subprocess, tempfile
from pathlib import Path
p=argparse.ArgumentParser(); p.add_argument('--launcher',required=True); p.add_argument('--agent',required=True); a=p.parse_args()
root=Path(tempfile.mkdtemp(prefix='uedt-migration-proof-')); platform='windows-x64' if os.name=='nt' else 'linux-x64'
app=root/'app'; app.mkdir(); (app/'game').write_bytes(b'fixture')
state=root/'state'/'demo'/platform; state.mkdir(parents=True)
config=root/'config.json'; config.write_text(json.dumps(dict(schemaVersion=1,projectId='demo',targetPlatform=platform,installDir=str(app),stateRootDir=str(root/'state'))))
(state/'installed-manifest.json').write_text(json.dumps(dict(appId='demo',version='1',platform=platform,entryPoint='game',files=[dict(path='game',size=7,sha256=hashlib.sha256(b'fixture').hexdigest())])))
def run(exe,*args): return subprocess.run([str(Path(exe).resolve()),*map(str,args)],env=dict(os.environ,UE_DT_AGENT_DATA_ROOT=str(root/'managed')),capture_output=True,encoding='utf-8',timeout=20)
dry=run(a.agent,'migrate','--config',config,'--dry-run'); assert dry.returncode==0,dry.stderr
assert json.loads(dry.stdout)['CanApply'] is False
before={str(p.relative_to(root)):p.read_bytes() for p in root.rglob('*') if p.is_file()}
assert run(a.agent,'migrate','--config',config,'--apply').returncode!=0
assert not (root/'managed').exists()
assert before=={str(p.relative_to(root)):p.read_bytes() for p in root.rglob('*') if p.is_file()}
assert run(a.launcher,'import-install','--config',config,'--destination-root',root/'target','--apply').returncode!=0
assert run(a.launcher,'runtime','recover','--config',config,'--confirm-stopped').returncode==0
copied=run(a.launcher,'import-install','--config',config,'--destination-root',root/'target','--apply'); assert copied.returncode==0,copied.stderr
target=Path(json.loads(copied.stdout)['destination']); assert (target/'game').read_bytes()==b'fixture'
assert (app/'game').read_bytes()==b'fixture'
assert run(a.launcher,'import-install','--config',config,'--destination-root',root/'target','--apply').returncode!=0
print('PASS: dry-run, apply refusal without writes, stopped import and no overwrite: '+str(root))
