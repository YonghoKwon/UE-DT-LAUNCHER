"""Published-process promotion proof. Private fixture/keys are never release artifacts."""
from ci_text import configure_console
configure_console()
import argparse
import hashlib
import json
from pathlib import Path
import re
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


def checked_document(raw, field):
    document = json.loads(raw)
    prefix, separator, tail = raw.rpartition(f',"{field}":"'.encode())
    digest = document[field]
    if (not separator or not re.fullmatch(r'[0-9a-f]{64}', digest) or
            tail != digest.encode() + b'"}' or
            hashlib.sha256(prefix + separator + b'"}').hexdigest() != digest):
        raise ValueError(f'Authority document has an invalid {field}')
    return document


def read_authority_catalog(root):
    authority = root / 'authority'
    head = checked_document((authority / 'HEAD').read_bytes(), 'headSha256')
    generation = head['generation']
    catalog_reference = head['files']['catalog']
    if (head['schema'] != 1 or type(generation) is not int or generation < 0 or
            not re.fullmatch(r'catalog/[0-9]{6,19}\.json', catalog_reference) or
            int(Path(catalog_reference).stem) > generation or
            head['files']['events'] != f'events/{generation:06}.json'):
        raise ValueError('Invalid catalog or audit reference in HEAD')
    catalog_generation = int(Path(catalog_reference).stem)
    if catalog_reference != f'catalog/{catalog_generation:06}.json':
        raise ValueError('Catalog generation path is not canonical')
    catalog_raw = (authority / catalog_reference).read_bytes()
    if hashlib.sha256(catalog_raw).hexdigest() != head['sha256']['catalog']:
        raise ValueError('Catalog digest differs from HEAD')
    return head, catalog_reference, json.loads(catalog_raw)


def verify_promotion_authority(root, jobs, inspection):
    authority = root / 'authority'
    head, catalog_reference, catalog = read_authority_catalog(root)
    generation = head['generation']
    catalog_generation = int(Path(catalog_reference).stem)
    releases = [(row['id'], row['job']) for row in catalog['releases']]
    history = inspection['history']
    if (catalog['schemaVersion'] != 1 or catalog['promotionReady'] is not True or
            releases != list(jobs.items()) or catalog['promotions'] != history or
            inspection['revision'] != history[-1]['revision'] or
            inspection['recommendedRelease'] != history[-1]['releaseId']):
        raise ValueError('Catalog history or publications differ from the CLI result')

    observed = []
    for number in range(generation + 1):
        raw = (authority / f'events/{number:06}.json').read_bytes()
        if number == generation and hashlib.sha256(raw).hexdigest() != head['sha256']['events']:
            raise ValueError('Audit digest differs from HEAD')
        batch = checked_document(raw, 'batchSha256')
        if batch['schemaVersion'] != 1 or batch['generation'] != number:
            raise ValueError('Invalid audit batch generation')
        for ordinal, record in enumerate(batch['events']):
            if record['generation'] != number or record['ordinal'] != ordinal:
                raise ValueError('Invalid authority audit record position')
            action = record['action']
            if action in ('published', 'release-promoted'):
                observed.append((number, action, record['job']))
    if ([job for _, action, job in observed if action == 'published'] != list(jobs.values()) or
            [release for _, action, release in observed if action == 'release-promoted'] !=
            [event['releaseId'] for event in history] or observed[-1][0] != catalog_generation):
        raise ValueError('Authority audit differs from the CLI publication or promotion history')
    return catalog_reference, observed


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
    assert json.loads(run(server,'list',*config)) == []
    track=['--project-id','demo','--environment','prod','--channel','stable','--platform','windows-x64']
    source=root/'server'
    jobs={}
    def publish(version):
        upload=root/'server'/'incoming'/f'upload-{version}'; upload.mkdir(parents=True); archive=upload/'Windows.zip'
        with zipfile.ZipFile(archive,'x') as z: z.writestr('game.exe',b'non-executed promotion test payload')
        run(launcher,'release-metadata','--zip',archive,*track,'--version',version,'--entry-point','game.exe','--output',upload/'release.json')
        job=json.loads(run(server,'ingest',upload,*config)); run(server,'approve',job['id'],*config)
        jobs[f'demo/prod/stable/{version}/windows-x64']=job['id']
    publish('2.0.0')
    file_authority=(source/'authority'/'HEAD').is_file()
    if file_authority:
        _, reference, catalog = read_authority_catalog(source)
        # During S2–S5 the published jobs authority can coexist with the SQLite catalog.
        file_authority = not (reference == 'catalog/000000.json' and
                              not catalog['releases'] and not catalog['promotions'] and
                              (source/'distribution.db').is_file())
    if args.require_file_authority and not file_authority:
        raise RuntimeError('Published server did not commit the file catalog')
    initial=json.loads(run(server,'promotion','inspect',*track,*config)); assert initial['revision']==0 and initial['recommendedRelease'] is None
    promoted=json.loads(run(server,'promote',*track,'--version','2.0.0','--expected-revision','0','--reason','isolated validation',*config))
    publish('9.0.0'); publish('1.0.0')
    after=json.loads(run(server,'promotion','inspect',*track,*config)); assert after['recommendedRelease']==promoted['recommendedRelease'] and after['revision']==promoted['revision']
    before_retries=verify_promotion_authority(source,jobs,after) if file_authority else None
    same=json.loads(run(server,'promote',*track,'--version','2.0.0','--expected-revision',after['revision'],'--reason','repeat',*config)); assert same['revision']==after['revision']
    run(server,'promote',*track,'--version','1.0.0','--expected-revision','0','--reason','stale',*config,expected=1)
    if file_authority and verify_promotion_authority(source,jobs,same) != before_retries:
        raise ValueError('No-op or stale request changed the authority catalog or audit')
    final_inspection=same
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
                final_inspection=json.loads(run(server,'promote',*track,'--version','1.0.0','--expected-revision',after['revision'],'--reason','explicit previous version',*config))
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
        verify_promotion_authority(source,jobs,final_inspection)
        pure = source
        if (source/'distribution.db').is_file():
            pure = root/'pure'
            shutil.copytree(source, pure, ignore=shutil.ignore_patterns('distribution.db', 'distribution.db-*'))
        migration(pure,0)
    if (source/'authority'/'HEAD').is_file():
        mixed=root/'mixed'; shutil.copytree(source,mixed)
        if not (mixed/'distribution.db').is_file():
            (mixed/'distribution.db').write_bytes(b'SQLite format 3\0'+bytes(80))
        migration(mixed,1)
        damaged=root/'damaged'
        shutil.copytree(source, damaged, ignore=shutil.ignore_patterns('distribution.db', 'distribution.db-*'))
        (damaged/'authority'/'HEAD').write_text('{}',encoding='utf-8')
        migration(damaged,1)
    summary['fileAuthorityVerified']=file_authority
    write(root/'summary.json',summary); print('PASS: '+str(root))


if __name__=='__main__':main()
