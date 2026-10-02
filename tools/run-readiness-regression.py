"""Non-interactive published E2E; only an allowlisted summary can leave the private root."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

SCENARIOS = ('fixture-preparation', 'offline-inventory', 'recommendation', 'exact', 'missing-project',
             'installation-execution-repair', 'managed-diagnostics', 'revocation')


def sanitized_summary(source, platform, successful, evidence=None, failure_scenario='fixture-preparation'):
    if not re.fullmatch(r'[0-9a-fA-F]{40}', source): source = 'unavailable'
    if platform not in ('Windows', 'Linux'): platform = 'unavailable'
    results = {name: 'not-run' for name in SCENARIOS}
    if successful:
        proofs = (evidence or {}).get('readiness_proofs', [])
        if {p.get('mode') for p in proofs} != {'portable', 'managed-agent'}: raise ValueError('Missing deployment-mode proof')
        for key in ('offline_inventory_unchanged', 'recommendation_checked', 'exact_checked', 'missing_project_checked'):
            if not all(p.get(key) is True for p in proofs): raise ValueError('Incomplete readiness proof')
        if not ((evidence or {}).get('versions_installed_and_launched') == 2 and evidence.get('repair') is True
                and evidence.get('managed_asset_and_doctor') is True and evidence.get('readiness_revocation_rejected') is True):
            raise ValueError('Incomplete process proof')
        results = {name: 'passed' for name in SCENARIOS}
    else:
        if failure_scenario not in SCENARIOS: failure_scenario = 'fixture-preparation'
        results[failure_scenario] = 'failed'
    rows = [{'name': key, 'status': value} for key, value in results.items()]
    return {'schemaVersion': 1, 'source': source.lower(), 'platform': platform, 'success': bool(successful),
            'checks': rows, 'counts': {status: sum(r['status'] == status for r in rows) for status in ('passed', 'failed', 'not-run')}}


def main():
    parser = argparse.ArgumentParser()
    for name in ('launcher', 'agent', 'server', 'output'): parser.add_argument('--' + name, required=True)
    parser.add_argument('--source-sha')
    args = parser.parse_args()
    source = args.source_sha or os.environ.get('GITHUB_SHA', '')
    if not source:
        result = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True)
        source = result.stdout.strip() if result.returncode == 0 else 'unavailable'
    root = Path(tempfile.mkdtemp(prefix='uedt-readiness-'))
    command = [sys.executable, str(Path(__file__).with_name('test-intranet-auth.py')),
               '--root', str(root), '--readiness-proof', '--defer-promotion']
    for name in ('launcher', 'agent', 'server'): command.extend(['--' + name, str(Path(getattr(args, name)).resolve())])
    success = False; evidence = None
    try:
        with (root / 'private-runner.log').open('w', encoding='utf-8') as log:
            process = subprocess.run(command, stdout=log, stderr=log, timeout=480,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        success = process.returncode == 0
        if success: evidence = json.loads((root / 'summary.json').read_text(encoding='utf-8'))
        report = sanitized_summary(source, 'Windows' if os.name == 'nt' else 'Linux', success, evidence,
            json.loads((root / 'readiness-progress.json').read_text()).get('phase', '') if (root / 'readiness-progress.json').exists() else '')
    except (OSError, ValueError, subprocess.TimeoutExpired):
        success = False
        report = sanitized_summary(source, 'Windows' if os.name == 'nt' else 'Linux', False)
    output = Path(args.output).resolve(); output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(('PASS' if success else 'FAIL') + ': published readiness regression; sanitized summary written')
    return 0 if success else 1


if __name__ == '__main__': sys.exit(main())
