"""Functional CI runner. Private synthetic roots/logs never become artifacts."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def main():
    parser=argparse.ArgumentParser()
    for name in ('launcher','agent','server','output'):parser.add_argument('--'+name,required=True)
    args=parser.parse_args();tools=Path(__file__).resolve().parent
    private=Path(tempfile.mkdtemp(prefix='uedt-headless-'));fixture=private/'fixture'
    binaries={name:str(Path(getattr(args,name)).resolve()) for name in ('launcher','agent','server')}
    checks=[]
    try:
        def run(name,command):
            with (private/(name+'.log')).open('w',encoding='utf-8') as log:
                result=subprocess.run(command,stdout=log,stderr=log,timeout=480,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
            checks.append({'name':name,'success':result.returncode==0})
            if result.returncode:raise RuntimeError('Functional proof failed')
        run('auth-operations-schedule',[sys.executable,str(tools/'test-intranet-auth.py'),'--root',str(fixture),
            *[part for name,path in binaries.items() for part in ('--'+name,path)],'--operation-proof','--credential-proof','--schedule-proof','--service-proof'])
        evidence=json.loads((fixture/'summary.json').read_text())
        if not all(evidence.get(name) is True for name in ('owned_operation_cancellation','credential_lifecycle','read_only_scheduled_check','explicit_service_selection_proof','revoked_key_rejected')):
            raise RuntimeError('Incomplete evidence')
        run('offline-maintenance',[sys.executable,str(tools/'test-distribution-maintenance.py'),'--server',binaries['server'],
            '--config',str(fixture/'server.json'),'--output',str(private/'maintenance')])
        success=True
    except (OSError,ValueError,subprocess.TimeoutExpired,RuntimeError):
        success=False
    source=os.environ.get('GITHUB_SHA','')
    if not source:
        found=subprocess.run(['git','rev-parse','HEAD'],capture_output=True,text=True);source=found.stdout.strip()
    report={'schemaVersion':1,'source':source if re.fullmatch('[0-9a-f]{40}',source) else 'unavailable',
        'platform':'Windows' if os.name=='nt' else 'Linux','success':success,'checks':checks,
        'count':sum(c['success'] for c in checks)}
    output=Path(args.output);output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(('PASS' if success else 'FAIL')+': headless operations; allowlisted summary only')
    return 0 if success else 1


if __name__=='__main__':sys.exit(main())
