"""Published-process promotion proof. Private fixture/keys are never release artifacts."""
from ci_text import configure_console
configure_console()
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile
import base64
import socket
import urllib.request
import time


def write_command_log(path, result, secret=False):
    # Credential provisioning stdout is consumed in memory only, never retained in a command log.
    path.write_text('secret output omitted\n' if secret else result.stdout + result.stderr, encoding='utf-8')


def inventory(root):
    return {str(path.relative_to(root)): ('directory' if path.is_dir() else hashlib.sha256(path.read_bytes()).hexdigest())
            for path in root.rglob('*')}


def main():
    p=argparse.ArgumentParser(); p.add_argument('--root',required=True); p.add_argument('--server',required=True); p.add_argument('--launcher',required=True); p.add_argument('--catalog-proof',action='store_true')
    p.add_argument('--require-file-authority',action='store_true')
    args=p.parse_args(); root=Path(args.root).resolve(); server=Path(args.server).resolve(); launcher=Path(args.launcher).resolve()
    if root.exists(): raise ValueError('Use a new private test root')
    root.mkdir(parents=True); counter=0
    def run(binary,*values,expected=0):
        nonlocal counter; counter+=1
        result=subprocess.run([str(binary),*map(str,values)],capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=60)
        write_command_log(root/f'command-{counter:02d}.log', result, secret=values[:1] == ('token-issue',))
        if result.returncode!=expected and not(expected==1 and result.returncode!=0): raise RuntimeError(f'Unexpected command result {counter}')
        return result.stdout
    def write(path,value): path.write_text(json.dumps(value), encoding='utf-8')
    run(launcher,'generate-signing-key','--private-key',root/'sign.pem','--public-key',root/'public.pem')
    with socket.socket() as reservation: reservation.bind(('127.0.0.1',0)); port=reservation.getsockname()[1]
    write(root/'server.json',{'root':str(root/'server'),'publicUrl':'https://localhost:19443','listenUrl':'http://127.0.0.1:'+str(port),
        'signingKeyPath':str(root/'sign.pem'),'policyPath':str(root/'policy.json')})
    config=['--config',root/'server.json']
    track=['--project-id','demo','--environment','prod','--channel','stable','--platform','windows-x64']
    run(server,'list',*config)
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
    source=root/'server'
    file_authority=(source/'authority'/'HEAD').is_file()
    if args.require_file_authority and not file_authority:
        raise RuntimeError('Published server has no file authority HEAD')
    summary={'publishedProcesses':True,'approvalDoesNotPromote':True,'higherAndOlderApprovalPreserveRecommendation':True,
        'sameTargetNoOp':True,'staleRevisionRejected':True,'fileAuthorityVerified':False,'companyValidation':False}
    if args.catalog_proof:
        write(root/'policy.json',{'clients':[{'id':'all','addresses':['127.0.0.1/32'],'grants':[{'projectId':'demo','environment':'prod','channel':'stable'}]},
            {'id':'old','addresses':['127.0.0.1/32'],'grants':[{'projectId':'demo','environment':'prod','channel':'stable','versions':['2.0.0']}]}]})
        all_token=run(server,'token-issue','all',*config).strip(); old_token=run(server,'token-issue','old',*config).strip()
        with (root/'server.log').open('w', encoding='utf-8') as log:
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
    def migration(target, expected):
        path=root/(target.name+'-migration.json')
        write(path,{'root':str(target)})
        before=inventory(target)
        for mode in ('--dry-run','--apply','--apply'):
            result=run(server,'promotion','migrate',mode,'--config',path,expected=expected)
            if expected==0:
                assert json.loads(result)=={'ready':True,'applyRequired':False,'baselineReleases':[]}
            assert before==inventory(target)

    legacy=root/'legacy'; legacy.mkdir()
    (legacy/'distribution.db').write_bytes(b'SQLite format 3\0'+bytes(80))
    migration(legacy,1)
    summary['legacyRejectedUnchanged']=True
    if file_authority:
        migration(source,0)
        mixed=root/'mixed'; shutil.copytree(source,mixed)
        (mixed/'distribution.db').write_bytes(b'SQLite format 3\0'+bytes(80))
        migration(mixed,1)
        damaged=root/'damaged'; shutil.copytree(source,damaged)
        (damaged/'authority'/'HEAD').write_text('{}',encoding='utf-8')
        migration(damaged,1)
        summary['fileAuthorityVerified']=True
    write(root/'summary.json',summary); print('PASS: '+str(root))


if __name__=='__main__':main()
