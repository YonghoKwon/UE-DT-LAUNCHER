"""Offline backup/controlled restore of an OWNED synthetic fixture, never company data."""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--server',required=True,type=Path)
    parser.add_argument('--config',required=True,type=Path,help='Stopped synthetic server.json')
    parser.add_argument('--output',required=True,type=Path,help='New proof directory')
    args=parser.parse_args(); args.output.mkdir(parents=True,exist_ok=False)
    executable=str(args.server.resolve()); config=args.config.resolve()
    settings=json.loads(config.read_text(encoding='utf-8-sig'))
    if not str(settings['publicUrl']).startswith('http://127.0.0.1:') or not ('publish' in config.parts or config.parent.parent.name.startswith('uedt-headless-')):
        raise RuntimeError('Only an isolated loopback published fixture is allowed')
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    counter=0
    def run(*command,expected=0):
        nonlocal counter
        counter+=1
        result=subprocess.run([executable,*map(str,command),'--config',str(config)],capture_output=True,text=True,encoding='utf-8',timeout=120,creationflags=flags)
        (args.output/('private-command-'+str(counter)+'.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
        if (expected is None and result.returncode==0) or (expected is not None and result.returncode!=expected):raise RuntimeError('Maintenance command failed: '+str(command[:2]))
        return result.stdout
    backup=args.output/'backup'; target=args.output/'restored'
    run('backup','plan'); run('backup','create','--output',backup); run('backup','verify','--backup',backup)
    run('restore','stage','--backup',backup,'--target',target)
    issued=run('token-issue','pc-test').strip()
    metadata=json.loads(run('token-list'))[-1];run('token-revoke-id','--id',metadata['id'])
    with (args.output/'server-private.log').open('w') as log:
        owned=subprocess.Popen([executable,'serve','--config',str(config)],stdout=log,stderr=log,creationflags=flags)
        try:
            import socket
            from urllib.parse import urlsplit
            address=urlsplit(settings['listenUrl']); deadline=time.monotonic()+15
            while True:
                try:
                    with socket.create_connection((address.hostname,address.port),timeout=.2):break
                except OSError:
                    if owned.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Owned server did not start')
                    time.sleep(.05)
            failure=subprocess.run([executable,'backup','create','--output',str(args.output/'must-not-exist'),'--config',str(config)],capture_output=True,timeout=30,creationflags=flags)
            if failure.returncode==0 or (args.output/'must-not-exist').exists():raise RuntimeError('Live server did not exclude backup')
        finally:
            owned.terminate();owned.wait(timeout=15)
    run('restore','activate','--target',target,'--confirm')
    import uuid
    temporary_name='processing/headless-cleanup-'+uuid.uuid4().hex[:8]
    temporary=Path(settings['root'])/temporary_name;temporary.mkdir(exist_ok=False)
    (temporary/'data.bin').write_bytes(b'owned-temporary')
    plan=args.output/'retention-plan.json'
    run('retention','plan','--temporary',temporary_name,'--output',plan)
    (temporary/'data.bin').write_bytes(b'changed-plan')
    run('retention','apply','--plan',plan,'--confirm',expected=None)
    if not temporary.exists():raise RuntimeError('Stale plan deleted content')
    run('retention','plan','--temporary',temporary_name,'--output',plan)
    run('retention','apply','--plan',plan,'--confirm')
    if temporary.exists():raise RuntimeError('Confirmed cleanup failed')
    with sqlite3.connect(target/'distribution.db') as db:
        if db.execute('SELECT revoked FROM tokens WHERE management_id=?',(metadata['id'],)).fetchone()!=(1,):raise RuntimeError('Restore resurrected credential')
    summary={'schemaVersion':1,'backup_verified':True,'live_exclusion':True,'staged_fence':True,'latest_revocation_preserved':True,
             'private_key_included':False,'source_loss_recovery_tested':False,'stale_retention_plan_rejected':True,'confirmed_temp_cleanup':True}
    (args.output/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    print('PASS: published offline backup and controlled restore')


if __name__=='__main__':main()
