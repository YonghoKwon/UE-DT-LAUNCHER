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
    result=json.loads(run(launcher,'scheduled-check','--config',path))
    if result['status']!='checked' or inventory()!=before:
        raise RuntimeError('Scheduled check mutated installation or failed')
    probe['scheduledCheck']['enabled']=False;path.write_text(json.dumps(probe),encoding='utf-8')
    if json.loads(run(launcher,'scheduled-check','--config',path))['status']!='disabled':
        raise RuntimeError('Disabled schedule was executed')
    return True
