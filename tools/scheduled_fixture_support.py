"""No task/service registration. Published one-shot checks of isolated fixtures."""
import hashlib
import json


def prove_schedule(run,launcher,client,config):
    def inventory():
        roots=[client/'apps',client/'state']
        return {str(path.relative_to(client)):hashlib.sha256(path.read_bytes()).hexdigest()
                for root in roots if root.exists() for path in root.rglob('*')
                if path.is_file() and path.name!='catalog-trust.json'}
    probe=dict(config,scheduledCheck={'enabled':True,'intervalSeconds':3600})
    path=client/'scheduled.json';path.write_text(json.dumps(probe),encoding='utf-8')
    before=inventory()
    agent_config=client/'agent/config/launcher.config.json';original=None
    if config.get('deploymentMode')=='managed-agent':
        original=agent_config.read_bytes();default=json.loads(original);default['requestedVersion']='1.0.0';agent_config.write_text(json.dumps(default),encoding='utf-8')
    try:result=json.loads(run(launcher,'scheduled-check','--config',path))
    finally:
        if original is not None:agent_config.write_bytes(original)
    if result['status'] not in ('checked','verification-pending') or result.get('installation') is None or inventory()!=before:
        raise RuntimeError('Scheduled check mutated installation or failed')
    if result['installation']['availableVersion']!=config['requestedVersion']:
        raise RuntimeError('Schedule mixed configured and protected default releases')
    probe['scheduledCheck']['enabled']=False;path.write_text(json.dumps(probe),encoding='utf-8')
    if json.loads(run(launcher,'scheduled-check','--config',path))['status']!='disabled':
        raise RuntimeError('Disabled schedule was executed')
    return True
