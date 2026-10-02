"""Published-process promotion proof. Private fixture/keys are never release artifacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile


def main():
    p=argparse.ArgumentParser(); p.add_argument('--root',required=True); p.add_argument('--server',required=True); p.add_argument('--launcher',required=True)
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
    write(root/'server.json',{'root':str(root/'server'),'publicUrl':'https://localhost:19443','listenUrl':'http://127.0.0.1:18520',
        'signingKeyPath':str(root/'sign.pem'),'policyPath':str(root/'policy.json')})
    config=['--config',root/'server.json']
    track=['--project-id','demo','--environment','prod','--channel','stable','--platform','windows-x64']
    def publish(version):
        upload=root/f'upload-{version}'; upload.mkdir(); archive=upload/'Windows.zip'
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
    write(root/'summary.json',summary); print('PASS: '+str(root))


if __name__=='__main__':main()
