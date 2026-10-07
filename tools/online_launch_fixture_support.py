"""Published launch denial probes. Only isolated fixture policy/config/keys are changed."""
import hashlib,json


def prove_online_launch(run,launcher,server,root,client,config):
    paths=[client/'config.json']
    if config.get('deploymentMode')=='managed-agent':paths.append(client/'agent/config/launcher.config.json')
    originals={path:path.read_bytes() for path in paths if path.exists()}
    policy=root/'policy.json';policy_bytes=policy.read_bytes()
    def protected():
        return {str(p.relative_to(client)):hashlib.sha256(p.read_bytes()).hexdigest()
                for folder in (client/'apps',client/'state') if folder.exists() for p in folder.rglob('*')
                if p.is_file() and p.name not in ('update.lock','catalog-trust.json')}
    def deny(name):
        before=protected();run(launcher,'run','--config',client/'config.json',expected=1)
        if before!=protected():raise RuntimeError('Denied online launch changed protected installation: '+name)
        checks.append(name)
    checks=[]
    try:
        current=json.loads(policy_bytes)
        for pc in current['clients']:pc['addresses']=['10.255.255.255/32']
        policy.write_text(json.dumps(current),encoding='utf-8');deny('current-ip-policy')
        policy.write_bytes(policy_bytes)
        for path,data in originals.items():
            selection=json.loads(data);selection.update(versionPolicy='exact',requestedVersion='not-published')
            path.write_text(json.dumps(selection),encoding='utf-8')
        deny('missing-exact-release')
        for path,data in originals.items():path.write_bytes(data)
        run(launcher,'credential','keygen','--name','launch-expired','--key-id','launch-expired-key','--public-out',root/'launch-expired.json')
        run(server,'client-key','add','--client','pc-test','--public-key',root/'launch-expired.json','--expires-at','2000-01-01T00:00:00Z','--config',root/'server.json')
        for path,data in originals.items():
            selection=json.loads(data)
            if 'security' in selection:selection['security']['credentialName']='launch-expired'
            path.write_text(json.dumps(selection),encoding='utf-8')
        deny('expired-device-key')
    finally:
        policy.write_bytes(policy_bytes)
        for path,data in originals.items():path.write_bytes(data)
    return {'checks':checks,'protectedUnchanged':True,'existingRunningChild':'not-tested','networkDisconnect':'separate-e2e'}
