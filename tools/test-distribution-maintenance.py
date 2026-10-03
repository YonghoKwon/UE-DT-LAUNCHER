"""Offline backup/controlled restore of an OWNED synthetic fixture, never company data."""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time
from fixture_contract import claim, inside, verify


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--server',required=True,type=Path)
    parser.add_argument('--config',required=True,type=Path,help='Stopped synthetic server.json')
    parser.add_argument('--output',required=True,type=Path,help='New proof directory')
    parser.add_argument('--launcher',type=Path,help='Matching published launcher for authenticated restored install proof')
    args=parser.parse_args()
    executable=str(args.server.resolve()); config=args.config.resolve()
    settings=verify(config.parent,config,executable)
    from gui_fixture_evidence import validate_origin
    validate_origin(settings['publicUrl']);validate_origin(settings['listenUrl'])
    claim(args.output)
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    counter=0
    def run(*command,expected=0):
        nonlocal counter
        counter+=1
        result=subprocess.run([executable,*map(str,command),'--config',str(config)],capture_output=True,text=True,encoding='utf-8',timeout=120,creationflags=flags)
        (args.output/('private-command-'+str(counter)+'.log')).write_text('secret output omitted\n' if command[:1]==('token-issue',) else result.stdout+result.stderr,encoding='utf-8')
        if (expected is None and result.returncode==0) or (expected is not None and result.returncode!=expected):raise RuntimeError('Maintenance command failed: '+str(command[:2]))
        return result.stdout
    backup=args.output/'backup'; target=args.output/'restored'
    run('backup','plan'); run('backup','create','--output',backup); run('backup','verify','--backup',backup)
    run('restore','stage','--backup',backup,'--target',target)
    target_settings={**settings,'root':str(target)}
    target_config=args.output/'target-server.json';target_config.write_text(json.dumps(target_settings),encoding='utf-8')
    denied=subprocess.run([executable,'serve','--config',str(target_config)],capture_output=True,timeout=15,creationflags=flags)
    if denied.returncode==0:raise RuntimeError('Staged target was served')
    private_key=Path(settings['signingKeyPath']).read_bytes()
    for file in (backup/'content').rglob('*'):
        if file.is_file() and file.stat().st_size==len(private_key) and file.read_bytes()==private_key:raise RuntimeError('Private signing key entered backup')
    run('token-issue','pc-test')
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
    target_settings['policyPath']=str(target/'restored-policy.json');target_config.write_text(json.dumps(target_settings),encoding='utf-8')
    restored_authenticated=False
    if args.launcher:
        launcher=str(args.launcher.resolve());registration=args.output/'restored-device-public.json';key_id='restore-probe-'+__import__('uuid').uuid4().hex[:8]
        environment={**os.environ,'UE_DT_AGENT_DATA_ROOT':str(args.output/'probe-private')}
        def client(*arguments):
            result=subprocess.run([launcher,*map(str,arguments)],env=environment,capture_output=True,timeout=120,creationflags=flags)
            if result.returncode:raise RuntimeError('Restored client proof failed')
        client('credential','keygen','--name','restore-probe','--key-id',key_id,'--storage','portable','--public-out',registration)
        result=subprocess.run([executable,'client-key','add','--client','pc-test','--public-key',str(registration),'--config',str(target_config)],capture_output=True,timeout=30,creationflags=flags)
        if result.returncode:raise RuntimeError('Restored device registration failed')
        client_config=args.output/'restored-client.json'
        config_value={'schemaVersion':3,'deploymentMode':'portable','distributionServerUrl':settings['publicUrl'],'projectId':'demo','clientProfile':'developer','environment':'prod','channel':'stable','versionPolicy':'exact','requestedVersion':'2.0.0','targetPlatform':'windows-x64' if os.name=='nt' else 'linux-x64','installDir':str(args.output/'probe-apps'),'stateRootDir':str(args.output/'probe-state'),'logDir':str(args.output/'probe-logs'),'requireSignedManifests':True,'launchAfterUpdate':False,'security':{'authenticationMode':'request-signature-v1','requireHttps':False,'credentialName':'restore-probe','allowedDownloadHosts':['127.0.0.1'],'trustedSigningKeys':[{'keyId':settings.get('signingKeyId','release-1'),'publicKeyPath':str(config.parent/'public.pem')}]}}
        client_config.write_text(json.dumps(config_value),encoding='utf-8')
        with (args.output/'restored-server-private.log').open('w') as log:
            restored=subprocess.Popen([executable,'serve','--config',str(target_config)],stdout=log,stderr=log,creationflags=flags)
            try:
                deadline=time.monotonic()+15
                while True:
                    try:
                        with socket.create_connection((address.hostname,address.port),timeout=.2):break
                    except OSError:
                        if restored.poll() is not None or time.monotonic()>deadline:raise RuntimeError('Restored server did not start')
                        time.sleep(.05)
                client('run','--config',client_config,'--no-launch');restored_authenticated=True
            finally:restored.terminate();restored.wait(timeout=15)
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
             'private_key_included':False,'source_loss_recovery_tested':False,'stale_retention_plan_rejected':True,'confirmed_temp_cleanup':True,'restored_authenticated_install':restored_authenticated}
    (args.output/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
    print('PASS: published offline backup and controlled restore')


if __name__=='__main__':main()
