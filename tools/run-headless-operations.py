"""Functional CI runner. Private synthetic roots/logs never become artifacts."""
from ci_text import configure_console
configure_console()
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from evidence_contract import Evidence
from fixture_contract import claim


AUTH_PHASES = frozenset((
    'provisioning', 'installation-and-repair', 'service-selection', 'agent-setup',
    'operation-cancellation', 'credential-lifecycle', 'scheduled-check', 'revocation',
    'fixture-shutdown',
))


def auth_phase(fixture):
    try:
        with (fixture/'headless-phase').open(encoding='ascii') as marker:
            phase = marker.read(64)
    except (OSError, UnicodeError):
        return 'unavailable'
    return phase if phase in AUTH_PHASES else 'unavailable'


def main():
    parser=argparse.ArgumentParser()
    for name in ('launcher','agent','server','output'):parser.add_argument('--'+name,required=True)
    args=parser.parse_args();tools=Path(__file__).resolve().parent
    private=Path(tempfile.mkdtemp(prefix='uedt-headless-'));fixture=private/'fixture'
    claim(private)
    binaries={name:str(Path(getattr(args,name)).resolve()) for name in ('launcher','agent','server')}
    evidence_report=Evidence(args.output,binaries,['auth-operations-schedule','offline-maintenance','catalog-restart'])
    try:
        def run(name,command,required_evidence=()):
            evidence_report.start(name)
            code = None
            try:
                with (private/(name+'.log')).open('w',encoding='utf-8') as log:
                    result=subprocess.run(command,stdout=log,stderr=log,timeout=480,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
                code = result.returncode
                failure = 'process-failed' if code else None
                if not failure and required_evidence:
                    evidence=json.loads((fixture/'summary.json').read_text(encoding='utf-8'))
                    if not isinstance(evidence,dict) or not all(evidence.get(key) is True for key in required_evidence):
                        failure = 'invalid-evidence'
            except subprocess.TimeoutExpired:
                failure = 'timeout'
            except (OSError, ValueError):
                failure = 'invalid-evidence' if code == 0 else 'process-failed'
            evidence_report.finish(name,failure is None,code,failure)
            if failure:
                phase = f' phase={auth_phase(fixture)}' if required_evidence else ''
                print(f'Headless check failed: check={name}{phase} reason={failure} exitCode={code}', flush=True)
                raise RuntimeError('Functional proof failed')
        run('auth-operations-schedule',[sys.executable,str(tools/'test-intranet-auth.py'),'--root',str(fixture),
            *[part for name,path in binaries.items() for part in ('--'+name,path)],'--operation-proof','--credential-proof','--schedule-proof','--service-proof'],
            required_evidence=('owned_operation_cancellation','credential_lifecycle','read_only_scheduled_check',
                               'explicit_service_selection_proof','revoked_key_rejected'))
        run('offline-maintenance',[sys.executable,str(tools/'test-distribution-maintenance.py'),'--server',binaries['server'],
            '--config',str(fixture/'server.json'),'--output',str(private/'maintenance'),'--launcher',binaries['launcher']])
        run('catalog-restart',[sys.executable,str(tools/'test-catalog-sequence-restart.py'),'--server',binaries['server'],
            '--config',str(fixture/'server.json'),'--output',str(private/'sequence-restart'),'--client-id','pc-test'])
        success=True
    except (OSError,ValueError,subprocess.TimeoutExpired,RuntimeError):
        success=False
    evidence_report.complete(success)
    print(('PASS' if success else 'FAIL')+': headless operations; allowlisted summary only')
    return 0 if success else 1


if __name__=='__main__':sys.exit(main())
