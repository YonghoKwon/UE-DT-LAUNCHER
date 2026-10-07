"""Published CLI read-only validation in a private synthetic root."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid

p = argparse.ArgumentParser()
p.add_argument('--launcher', required=True)
args = p.parse_args()
launcher = str(Path(args.launcher).resolve())
root = Path(tempfile.mkdtemp(prefix='uedt-runtime-validation-'))
platform = 'windows-x64' if os.name == 'nt' else 'linux-x64'
config = root / 'config.json'
config.write_text(json.dumps(dict(schemaVersion=1, projectId='demo', targetPlatform=platform,
    installDir=str(root/'app'), stateRootDir=str(root/'state'), logDir=str(root/'logs'))))
def command(*args):
    return subprocess.run([launcher, 'runtime', *args, '--config', str(config)], capture_output=True, text=True, timeout=20)
assert command('inspect').returncode == 0
assert command('recover', '--dry-run').returncode == 0
assert sorted(x.name for x in root.iterdir()) == ['config.json'], list(root.iterdir())
state = root/'state'/'demo'/platform
state.mkdir(parents=True)
path = state/'runtime-state.json'
install = str(root/'app')
if os.name == 'nt': install = install.upper()
path.write_text(json.dumps(dict(installationId=hashlib.sha256(install.encode()).hexdigest())))
before = path.read_bytes()
result = command('inspect')
assert result.returncode == 0, result.stderr
assert json.loads(result.stdout)['state'] == 3, result.stdout
bad = command('recover','--confirm-stopped','--service-selection','--version','2.0.0')
assert bad.returncode != 0
assert before == path.read_bytes()
assert not (state/'update.lock').exists()
path.write_text(json.dumps(dict(schemaVersion=1,installationId=hashlib.sha256(install.encode()).hexdigest(),state=2,
    origin='supervised',attemptId=uuid.uuid4().hex,tokenHash='a'*64,manifestHash='b'*64,entryPoint=str(root/'app'/'game'),arguments=[],
    requester=dict(pid=1,creationId='test',executable=None,owner='test',session='test',administrator=False))))
null_peer=command('inspect'); assert null_peer.returncode==0,null_peer.stderr
assert json.loads(null_peer.stdout)['state']==3
print('PASS: read-only inspect/dry-run, incomplete-record Unknown, rejected selection unchanged: '+str(root))
