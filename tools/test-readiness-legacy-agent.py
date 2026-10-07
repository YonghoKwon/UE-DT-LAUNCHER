"""Run the new diagnostic client against a real older published console Agent."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from gui_fixture_evidence import agent_status
from readiness_fixture_support import inventory

p = argparse.ArgumentParser()
p.add_argument('--launcher', required=True)
p.add_argument('--legacy-agent', required=True)
p.add_argument('--root', required=True)
a = p.parse_args()
root = Path(a.root).resolve()
root.mkdir(parents=True, exist_ok=True)
if any(root.iterdir()): raise ValueError('Use a new isolated root')
endpoint = 'uedt-legacy-' + root.name if os.name == 'nt' else str(root / 'agent.sock')
env = dict(os.environ, UE_DT_AGENT_DATA_ROOT=str(root / 'private'), UE_DT_AGENT_ENDPOINT=endpoint)
config = root / 'client.json'
config.write_text(json.dumps({'deploymentMode': 'managed-agent', 'projectId': 'demo',
    'targetPlatform': 'windows-x64' if os.name == 'nt' else 'linux-x64',
    'installDir': str(root / 'private/apps'), 'stateRootDir': str(root / 'private/state')}))
flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
with (root / 'agent.log').open('w') as log:
    agent = subprocess.Popen([str(Path(a.legacy_agent).resolve())], env=env, stdout=log, stderr=log, creationflags=flags)
    try:
        deadline = time.monotonic() + 30
        while True:
            try:
                status = agent_status(endpoint)
                if status.get('success'): break
            except (OSError, ValueError): pass
            if agent.poll() is not None or time.monotonic() >= deadline: raise RuntimeError('Legacy Agent did not start')
            time.sleep(.1)
        assert 'read-only-doctor-v1' not in status.get('agentCapabilities', [])
        before = {name: inventory(root / 'private' / name) for name in ('config', 'state', 'apps', 'credentials')}
        result = subprocess.run([str(Path(a.launcher).resolve()), 'doctor', '--config', str(config)], env=env,
            capture_output=True, text=True, encoding='utf-8', timeout=15, creationflags=flags)
        assert result.returncode == 1
        report = json.loads(result.stdout)
        assert report['preparationState'] == 'action-required'
        assert any(c['code'] == 'client-upgrade-required' for c in report['checks'])
        assert before == {name: inventory(root / 'private' / name) for name in before}
        (root / 'summary.json').write_text(json.dumps({'upgrade_required': True, 'protected_inventory_unchanged': True,
            'launcherSha256': hashlib.sha256(Path(a.launcher).read_bytes()).hexdigest(),
            'legacyAgentSha256': hashlib.sha256(Path(a.legacy_agent).read_bytes()).hexdigest()}, indent=2))
        print('PASS: legacy Agent rejected without protected state changes')
    finally:
        if agent.poll() is None: agent.terminate(); agent.wait(timeout=10)
