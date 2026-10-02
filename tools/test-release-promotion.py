"""Published-process promotion proof. Private fixture/keys are never release artifacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile
import base64
import socket
import urllib.request
import time
import sqlite3


def main():
    p=argparse.ArgumentParser(); p.add_argument('--root',required=True); p.add_argument('--server',required=True); p.add_argument('--launcher',required=True); p.add_argument('--catalog-proof',action='store_true')
    args=p.parse_args(); root=Path(args.root).resolve(); server=Path(args.server).resolve(); launcher=Path(args.launcher).resolve()
    if root.exists(): raise ValueError('Use a new private test root')
    root.mkdir(parents=True); counter=0
    def run(binary,*values,expected=0):
        nonlocal counter; counter+=1
        result=subprocess.run([str(binary),*map(str,values)],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
        (root/f'command-{counter:02d}.log').write_text(result.stdout+result.stderr)
        if result.returncode!=expected and not(expected==1 and result.returncode!=0): raise RuntimeError(f'Unexpected command result {counter}')
        return result.stdout
    def write(path,value): path.write_text(json.dumps(value))
    run(launcher,'generate-signing-key','--private-key',root/'sign.pem','--public-key',root/'public.pem')
    with socket.socket() as reservation: reservation.bind(('127.0.0.1',0)); port=reservation.getsockname()[1]
    write(root/'server.json',{'root':str(root/'server'),'publicUrl':'https://localhost:19443','listenUrl':'http://127.0.0.1:'+str(port),
        'signingKeyPath':str(root/'sign.pem'),'policyPath':str(root/'policy.json')})
    config=['--config',root/'server.json']
    track=['--project-id','demo','--environment','prod','--channel','stable','--platform','windows-x64']
    def publish(version):
        upload=root/'server'/'incoming'/f'upload-{version}'; upload.mkdir(parents=True); archive=upload/'Windows.zip'
        with zipfile.ZipFile(archive,'x') as z: z.writestr('game.exe',b'non-executed promotion test payload')
        run(launcher,'release-metadata','--zip',archive,*track,'--version',version,'--entry-point','game.exe','--output',upload/'release.json')
        job=json.loads(run(server,'ingest',upload,*config)); run(server,'approve',job['id'],*config)
    publish('2.0.0')
    initial=json.loads(run(server,'promotion','inspect',*track,*config)); assert initial['revision']==0 and initial['recommendedRelease'] is None
    promoted=json.loads(run(server,'promote',*track,'--version','2.0.0','--expected-revision','0','--reason','isolated validation',*config))
    publish('9.0.0'); publish('1.0.0')
    after=json.loads(run(server,'promotion','inspect',*track,*config)); assert after['recommendedRelease']==promoted['recommendedRelease'] and after['revision']==promoted['revision']
    same=json.loads(run(server,'promote',*track,'--version','2.0.0','--expected-revision',after['revision'],'--reason','repeat',*config)); assert same['revision']==after['revision']
    run(server,'promote',*track,'--version','1.0.0','--expected-revision','0','--reason','stale',*config,expected=1)
    db=root/'server/distribution.db'; before=hashlib.sha256(db.read_bytes()).hexdigest()
    preview=json.loads(run(server,'promotion','migrate','--dry-run',*config)); assert preview['ready']
    assert before==hashlib.sha256(db.read_bytes()).hexdigest()
    summary={'publishedProcesses':True,'approvalDoesNotPromote':True,'higherAndOlderApprovalPreserveRecommendation':True,
        'sameTargetNoOp':True,'staleRevisionRejected':True,'dryRunDatabaseUnchanged':True,'companyValidation':False}
    if args.catalog_proof:
        write(root/'policy.json',{'clients':[{'id':'all','addresses':['127.0.0.1/32'],'grants':[{'projectId':'demo','environment':'prod','channel':'stable'}]},
            {'id':'old','addresses':['127.0.0.1/32'],'grants':[{'projectId':'demo','environment':'prod','channel':'stable','versions':['2.0.0']}]}]})
        all_token=run(server,'token-issue','all',*config).strip(); old_token=run(server,'token-issue','old',*config).strip()
        with (root/'server.log').open('w') as log:
            process=subprocess.Popen([str(server),'serve',*map(str,config)],stdout=log,stderr=log)
            try:
                def catalog(token,new=True):
                    req=urllib.request.Request('http://127.0.0.1:'+str(port)+'/api/v1/catalog'+('?selectionPolicy=explicit-promotion-v1' if new else ''),headers={'Authorization':'Bearer '+token})
                    with urllib.request.urlopen(req,timeout=10) as response: envelope=json.load(response)
                    return json.loads(base64.b64decode(envelope['payload']))
                for attempt in range(100):
                    try: a=catalog(all_token); break
                    except (OSError,urllib.error.URLError):
                        if process.poll() is not None: raise RuntimeError('Owned server exited')
                        time.sleep(.1)
                else: raise TimeoutError('Server did not become ready')
                releases=a['projects'][0]['releases']; assert len(releases)==3 and [r['version'] for r in releases if r['isLatest']]==['2.0.0']
                legacy=catalog(all_token,False); assert [r['version'] for r in legacy['projects'][0]['releases']]==['2.0.0']
                head=json.loads(run(server,'promote',*track,'--version','1.0.0','--expected-revision',after['revision'],'--reason','explicit previous version',*config))
                a=catalog(all_token); limited=catalog(old_token)
                assert [r['version'] for r in a['projects'][0]['releases'] if r['isLatest']]==['1.0.0']
                assert [r['version'] for r in limited['projects'][0]['releases'] if r['isLatest']]==['2.0.0']
                assert a['sequence']>legacy['sequence']; summary['catalogAndPerPcPromotionProof']=True
            finally:
                process.terminate(); process.wait(timeout=15)
    # Exercise offline schema3 migration with published CLI, preserving approved order.
    legacy=root/'legacy'; legacy.mkdir(); legacy_db=legacy/'distribution.db'
    with sqlite3.connect(db) as source, sqlite3.connect(legacy_db) as target: source.backup(target)
    with sqlite3.connect(legacy_db) as target: target.executescript('DROP TABLE promotions; DROP TABLE promotion_state; PRAGMA user_version=3;')
    legacy_config=root/'legacy-server.json'; write(legacy_config,{'root':str(legacy)})
    before=hashlib.sha256(legacy_db.read_bytes()).hexdigest()
    plan=json.loads(run(server,'promotion','migrate','--dry-run','--config',legacy_config))
    assert plan['applyRequired'] and before==hashlib.sha256(legacy_db.read_bytes()).hexdigest()
    run(server,'promotion','migrate','--apply','--config',legacy_config)
    run(server,'promotion','migrate','--apply','--config',legacy_config)
    history=json.loads(run(server,'promotion','inspect',*track,'--config',legacy_config))
    assert len(history['history'])==3 and all(e['kind']=='legacy-baseline' for e in history['history'])
    assert history['recommendedRelease'].endswith('/1.0.0/windows-x64') and len(list(legacy.glob('distribution.pre-v5-*.db')))==1
    summary['publishedOfflineMigrationProof']=True
    write(root/'summary.json',summary); print('PASS: '+str(root))


if __name__=='__main__':main()
