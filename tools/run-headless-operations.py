"""Functional CI runner. Private synthetic roots/logs never become artifacts."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from evidence_contract import Evidence


def main():
    parser=argparse.ArgumentParser()
    for name in ('launcher','agent','server','output'):parser.add_argument('--'+name,required=True)
    args=parser.parse_args();tools=Path(__file__).resolve().parent
    private=Path(tempfile.mkdtemp(prefix='uedt-headless-'));fixture=private/'fixture'
    binaries={name:str(Path(getattr(args,name)).resolve()) for name in ('launcher','agent','server')}
    evidence_report=Evidence(args.output,binaries,['auth-operations-schedule','offline-maintenance'])
    try:
        def run(name,command):
            evidence_report.start(name)
            with (private/(name+'.log')).open('w',encoding='utf-8') as log:
                try:result=subprocess.run(command,stdout=log,stderr=log,timeout=480,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
                except subprocess.TimeoutExpired:
                    evidence_report.finish(name,False,kind='timeout');raise
            evidence_report.finish(name,result.returncode==0,result.returncode,'process-failed' if result.returncode else None)
            if result.returncode:raise RuntimeError('Functional proof failed')
        run('auth-operations-schedule',[sys.executable,str(tools/'test-intranet-auth.py'),'--root',str(fixture),
            *[part for name,path in binaries.items() for part in ('--'+name,path)],'--operation-proof','--credential-proof','--schedule-proof','--service-proof'])
        evidence=json.loads((fixture/'summary.json').read_text())
        if not all(evidence.get(name) is True for name in ('owned_operation_cancellation','credential_lifecycle','read_only_scheduled_check','explicit_service_selection_proof','revoked_key_rejected')):
            raise RuntimeError('Incomplete evidence')
        run('offline-maintenance',[sys.executable,str(tools/'test-distribution-maintenance.py'),'--server',binaries['server'],
            '--config',str(fixture/'server.json'),'--output',str(private/'maintenance'),'--launcher',binaries['launcher']])
        success=True
    except (OSError,ValueError,subprocess.TimeoutExpired,RuntimeError):
        success=False
    evidence_report.complete(success)
    print(('PASS' if success else 'FAIL')+': headless operations; allowlisted summary only')
    return 0 if success else 1


if __name__=='__main__':sys.exit(main())
