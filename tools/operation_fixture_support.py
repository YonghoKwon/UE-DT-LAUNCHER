"""Owned synthetic process only. Never creates or operates a GUI."""
import hashlib
import json
from pathlib import Path
import subprocess
import time
import uuid
from gui_fixture_evidence import agent_status


def prove_operations(root, client, launcher, env, flags, run, platform):
    installation = client / 'apps/demo/prod/stable/2.0.0' / platform
    metadata = client / 'state/demo/prod/stable/2.0.0' / platform
    def inventory():
        selected = [installation, metadata / 'backups']
        files = [path for directory in selected if directory.exists() for path in directory.rglob('*') if path.is_file()]
        files += [metadata / name for name in ['installed-manifest.json', 'install-state.json', 'transaction.json'] if (metadata / name).is_file()]
        return {str(path.relative_to(client)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}
    before = inventory()
    identifier = uuid.uuid4().hex
    with (root / 'cancellation-client.log').open('w') as log:
        owned = subprocess.Popen([launcher, 'run', '--repair', '--no-launch', '--operation-id', identifier,
                                  '--config', str(client / 'developer.json')], env=env, stdout=log, stderr=log, creationflags=flags)
        try:
            deadline = time.monotonic() + 30
            while True:
                response = agent_status(env['UE_DT_AGENT_ENDPOINT'], {'command': 'operation-status', 'correlationId': uuid.uuid4().hex, 'operationId': identifier})
                if response.get('success') and response['operation']['phase'] == 'Downloading': break
                if owned.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError('Download-phase cancellation barrier was not reached')
                time.sleep(.01)  # Polling the explicit phase, not treating elapsed time as success.
            cancelled = agent_status(env['UE_DT_AGENT_ENDPOINT'], {'command': 'operation-cancel', 'correlationId': uuid.uuid4().hex, 'operationId': identifier})
            if not cancelled.get('success') or not cancelled['operation']['cancellationRequested']:
                raise RuntimeError('Owned cancellation was not acknowledged')
            owned.wait(timeout=60)
            final = json.loads(run(launcher, 'operation', 'status', '--id', identifier))
            if final['operation']['phase'] != 'Cancelled' or inventory() != before:
                raise RuntimeError('Cancellation did not preserve protected installation inventory')
            if not final['operation'].get('manifestSha256'):
                raise RuntimeError('Cancelled operation was not bound to its authenticated manifest')
            resumed = json.loads(run(launcher, 'operation', 'resume', '--id', identifier))
            if resumed['operation']['phase'] != 'Completed' or inventory() == {}:
                raise RuntimeError('Fresh authorized resume did not complete')
            discarded = json.loads(run(launcher, 'operation', 'discard', '--id', identifier))
            if discarded['operation']['phase'] != 'Discarded': raise RuntimeError('Discard failed')
        finally:
            if owned.poll() is None:
                owned.terminate(); owned.wait(timeout=10)
    return True
