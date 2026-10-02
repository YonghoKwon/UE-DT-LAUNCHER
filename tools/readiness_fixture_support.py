"""Published diagnostic proofs. This file is never included in product artifacts."""
import hashlib
import json
from pathlib import Path


def inventory(root):
    return {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else 'directory'
            for path in sorted(Path(root).rglob('*'))}


def prove_readiness(run, launcher, client, config, expect_unpromoted=False):
    path = client / 'readiness-config.json'
    original = dict(config)
    original.update(versionPolicy='latest', requestedVersion=None)
    path.write_text(json.dumps(original), encoding='utf-8')
    before = inventory(client)
    offline = json.loads(run(launcher, 'doctor', '--config', path))
    assert inventory(client) == before, 'Offline diagnostics mutated client files'
    assert offline['preparationState'] == 'verification-pending'
    online = json.loads(run(launcher, 'doctor', '--config', path, '--online'))
    assert online['healthy']
    codes = [check['code'] for check in online['checks']]
    assert ('no-promoted-release' if expect_unpromoted else 'release-available') in codes
    assert online['target']['projectId'] == 'demo'
    exact = dict(original, versionPolicy='exact', requestedVersion='1.0.0')
    path.write_text(json.dumps(exact), encoding='utf-8')
    report = json.loads(run(launcher, 'doctor', '--config', path, '--online'))
    assert any(c['code'] == 'release-available' for c in report['checks'])
    missing = dict(original, projectId='not-authorized')
    path.write_text(json.dumps(missing), encoding='utf-8')
    report = json.loads(run(launcher, 'doctor', '--config', path, '--online'))
    assert any(c['code'] == 'no-authorized-release' for c in report['checks'])
    assert report['preparationState'] == 'action-required' and report['healthy']
    path.write_text(json.dumps(original), encoding='utf-8')
    return {'mode': original['deploymentMode'], 'offline_inventory_unchanged': True,
            'recommendation_checked': True, 'exact_checked': True, 'missing_project_checked': True}
