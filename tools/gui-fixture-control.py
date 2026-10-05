"""Controls only a prepared synthetic GUI fixture. Never operates on company installations."""
import argparse, hashlib, json, os, re, subprocess, time, zipfile
from pathlib import Path
from promotion_fixture_support import promote
from gui_fixture_evidence import inside, verify_files, verify_cohort, preference_hash, restore_preferences, snapshot_preferences, fixture_mode, fixture_environment, record_control, agent_status, sha256,operation_root
from gui_fixture_safety import mutation_guard, preflight, product_json
p=argparse.ArgumentParser(); p.add_argument('--root',required=True)
p.add_argument('action',choices=['smoke','gui-general','gui-developer','snapshot','compare','damage','release','stop-agent','start-agent','stop-server','start-server','stop-all','status','approve-v2','promote-v1','diagnostics','verify','verify-backup','invalidate-preview','prefs-snapshot','prefs-record-owned','prefs-restore','error-401','error-403','error-empty','error-config','config-reset','policy-reset','error-trust','trust-reset','proxy-status','proxy-fast','proxy-throttle','proxy-fail-version','proxy-reset-errors','proxy-fail-catalog-after-commit','proxy-fail-catalog-after-restore','cache-off','preflight','case-start','case-finish','acceptance-summary'])
p.add_argument('--version',choices=['1.0.0','2.0.0'],default='1.0.0'); p.add_argument('--name',default='before')
p.add_argument('--scope',choices=['all','protected'],default='all',help='protected excludes transient staging/cache/runtime while retaining payload, manifests, state, backups and transaction journal')
p.add_argument('--attempt', help='Exact synthetic child marker id to release')
p.add_argument('--profile',choices=['general','developer'],default='general')
p.add_argument('--result',choices=['passed','failed','blocked','not-run'],default='not-run');p.add_argument('--proof',help='Fixture-relative sanitized GUI evidence JSON')
p.add_argument('--damage-cached-download',action='store_true',help='Test-only: corrupt the portable fixture synthetic cancellation.bin cache before an explicit repair')
a=p.parse_args(); root=Path(a.root).resolve()
assert (root/'summary.json').exists() and json.loads((root/'summary.json').read_text())['gui_prepared_empty']
fixture=verify_cohort(root)
control=inside(root,'control')
if a.action!='preflight':control.mkdir(exist_ok=True)
env=fixture_environment(root,fixture)
mode=fixture_mode(fixture)
exe=Path(fixture['binaries']['launcher']['path'])
platform=fixture.get('platform','windows-x64' if os.name=='nt' else 'linux-x64')
state=inside(root,'client/state/demo/prod/stable/'+a.version+'/'+platform)
app=inside(root,'client/apps/demo/prod/stable/'+a.version+'/'+platform)
def latest_backup():
    folder=inside(root,str((state/'backups').relative_to(root)))
    candidates=sorted(folder.iterdir(),reverse=True) if folder.exists() else []
    for path in candidates:
        if re.fullmatch(r'[0-9]{14}(?:-[0-9]{2})?',path.name) and path.is_dir():
            return inside(root,str(path.relative_to(root)))
    raise ValueError('No fixture backup exists')
def snapshot():
    roots=[app,state] if a.scope=='all' else [app,state/'backups']
    files=[f for folder in roots if folder.exists() for f in folder.rglob('*') if f.is_file()
           and not (f.parent==state and f.name=='update.lock')]
    if a.scope=='protected':files += [state/name for name in ('installed-manifest.json','install-state.json','transaction.json') if (state/name).is_file()]
    return {str(f.relative_to(root)):hashlib.sha256(inside(root,str(f.relative_to(root))).read_bytes()).hexdigest() for f in files}

def atomic_bytes(path,data):
    path=inside(root,str(path.relative_to(root)))
    temporary=path.with_name(path.name+'.fixture-tmp')
    if temporary.exists(): raise ValueError('Prior fixture write is incomplete')
    temporary.write_bytes(data);temporary.replace(path)

def read_proxy_metrics():
    path=inside(root,'control/proxy-metrics.json')
    for attempt in range(20):
        try:return path.read_text()
        except PermissionError:
            if attempt==19:raise
            time.sleep(.01)

if a.action in ('stop-agent','start-agent') and mode=='portable':
    raise ValueError('Portable fixture has no Agent process or IPC')
if a.action in ('case-start','case-finish','acceptance-summary'):
    from acceptance_ledger import record_case,summary
    if a.action=='acceptance-summary':print(json.dumps(summary(root,fixture,a.profile)))
    else:
        proof=json.loads(inside(root,a.proof).read_text()) if a.proof else None
        print(json.dumps(record_case(root,fixture,a.profile,a.name,'running' if a.action=='case-start' else a.result,proof)))
elif a.damage_cached_download:
    if a.action!='status' or mode!='portable' or not fixture.get('guiDownloadProof'):raise ValueError('Only a quiescent portable synthetic download cache is eligible')
    target_id='demo/prod/stable/'+a.version+'/'+platform
    records=[path for path in (state/'resume-cache').rglob('resume.json') if json.loads(inside(root,str(path.relative_to(root))).read_text()).get('releaseId')==target_id]
    candidates=[path.parent/(hashlib.sha256(b'cancellation.bin').hexdigest()+'.verified') for path in records]
    candidates=[inside(root,str(path.relative_to(root))) for path in candidates if path.is_file()]
    if len(candidates)!=1 or candidates[0].stat().st_size!=64*1024*1024:raise ValueError('Exact synthetic cache not found')
    with mutation_guard(root,fixture,a.version):
        with candidates[0].open('r+b') as stream:stream.write(b'fixture-cache-corruption')
    record_control(root,'damage-cached-download',version=a.version);print('Corrupted only the exact portable synthetic cached download')
elif a.action=='cache-off':
    paths=[root/'client/general.json',root/'client/developer.json']
    if mode=='managed':paths.append(root/'client/agent/config/launcher.config.json')
    with mutation_guard(root,fixture,a.version):
        for path in paths:
            value=json.loads(path.read_text());value.setdefault('performance',{})['resumeCacheBytes']=0
            atomic_bytes(path,json.dumps(value,indent=2).encode())
    record_control(root,a.action);print('Disabled only fixture resume cache; existing cache files preserved')
elif a.action=='preflight':
    print(json.dumps(preflight(root,fixture,a.version)))
elif a.action.startswith('proxy-'):
    if not fixture.get('guiDownloadProof'):raise ValueError('Not a synthetic throttled-download fixture')
    if a.action=='proxy-status':print(read_proxy_metrics())
    elif a.action=='proxy-fast':inside(root,'control/proxy-unthrottle').touch();record_control(root,a.action);print('Disabled only synthetic transfer throttling')
    elif a.action=='proxy-fail-version':inside(root,'control/proxy-fail-version').touch();record_control(root,a.action);print('Test-only version.txt HTTP503 after upstream authorization')
    elif a.action=='proxy-fail-catalog-after-restore':
        from restore_file_witness import arm
        print(json.dumps(arm(root,fixture,a.version)));record_control(root,a.action,version=a.version)
    elif a.action=='proxy-fail-catalog-after-commit':
        operations=operation_root(root,fixture)
        records=[f.stem for f in operations.glob('*.json')] if operations.exists() else []
        if len(records)>256:raise ValueError('Too many fixture operation records')
        manifest=inside(root,'server/releases/demo/prod/stable/'+a.version+'/'+platform+'/manifest.json')
        atomic_bytes(control/'proxy-fail-catalog-after-commit',json.dumps({'version':a.version,'platform':platform,'beforeIds':records,'command':'repair','manifestSha256':sha256(manifest),'operationId':None,'operationsRelative':str(operations.relative_to(root))}).encode())
        record_control(root,a.action,version=a.version);print('Armed one authorized Catalog failure after the next exact repair commit')
    elif a.action=='proxy-reset-errors':
        for name in ('proxy-fail-version','proxy-fail-catalog-after-commit','proxy-fail-catalog-after-restore'):inside(root,'control/'+name).unlink(missing_ok=True)
        record_control(root,a.action);print('Removed file and pending Catalog faults')
    else:inside(root,'control/proxy-unthrottle').unlink(missing_ok=True);record_control(root,a.action);print('Restored synthetic transfer throttling')
elif a.action=='prefs-snapshot':
    if (root/'ui-preferences-original.json').exists(): raise ValueError('Original preferences already captured')
    snapshot_preferences(root); print('Original preferences captured')
elif a.action=='prefs-record-owned':
    (control/'ui-preferences-owned.sha256').write_text(preference_hash())
    print('Recorded last explicitly test-owned preference value')
elif a.action=='prefs-restore':
    restore_preferences(root,(control/'ui-preferences-owned.sha256').read_text())
    print('Restored original preferences after hash guard')
elif a.action=='promote-v1':
    def promotion_run(*values):
        return subprocess.check_output([fixture['binaries']['server']['path'],*map(str,values)],env=env,text=True,encoding='utf-8')
    promote(promotion_run,root/'server.json','demo','1.0.0',platform)
    record_control(root,a.action)
    print('Promoted fixture v1; no client install/run performed')
elif a.action=='approve-v2':
    job=fixture['pendingJobs'].get('2.0.0')
    if not job: raise ValueError('No pending v2 in this fixture')
    from evidence_contract import atomic
    state={'schemaVersion':1,'jobId':job,'phase':'approve','success':False}
    atomic(control/'approval-status.json',state)
    result=subprocess.run([fixture['binaries']['server']['path'],'approve',job,'--config',str(root/'server.json')],env=env,capture_output=True,timeout=120,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    (control/'approval-private.log').write_bytes(result.stdout+result.stderr)
    state['exitCode']=result.returncode;atomic(control/'approval-status.json',state)
    if result.returncode:raise RuntimeError('Fixture approval failed; private result retained')
    record_control(root,'approve-v2-committed',jobId=job)
    state['phase']='promote';atomic(control/'approval-status.json',state)
    def promotion_run(*values):
        return subprocess.check_output([fixture['binaries']['server']['path'],*map(str,values)],env=env,text=True,encoding='utf-8')
    promote(promotion_run,root/'server.json','demo','2.0.0',platform)
    state.update(phase='complete',success=True);atomic(control/'approval-status.json',state)
    record_control(root,'approve-v2',jobId=job)
    print('Approved fixture v2')
elif a.action=='error-config':
    path=inside(root,'client/'+a.profile+'.json');original=control/('original-'+a.profile+'.json');proof=control/(a.profile+'-config-fault.json')
    if proof.exists(): raise ValueError('Configuration failure is already prepared')
    original.write_bytes(path.read_bytes())
    atomic_bytes(path,b'{"privateKeyPem":"GUI-SENTINEL",BROKEN')
    proof.write_text(json.dumps({'originalSha256':sha256(original),'injectedSha256':sha256(path)}))
    record_control(root,a.action,profile=a.profile);print('Changed only the selected fixture display configuration')
elif a.action=='config-reset':
    path=inside(root,'client/'+a.profile+'.json');original=inside(control,'original-'+a.profile+'.json');proof=inside(control,a.profile+'-config-fault.json')
    evidence=json.loads(proof.read_text())
    if sha256(path)!=evidence['injectedSha256'] or sha256(original)!=evidence['originalSha256']:raise ValueError('Configuration changed after fault injection; refusing overwrite')
    atomic_bytes(path,original.read_bytes());proof.unlink()
    record_control(root,a.action,profile=a.profile);print('Restored selected fixture display configuration after hash verification')
elif a.action=='error-401':
    subprocess.run([fixture['binaries']['server']['path'],'client-key','revoke','--key-id','pc-test-key','--config',str(inside(root,'server.json'))],env=env,check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    (control/'auth-key-revoked').touch();record_control(root,a.action)
    print('Revoked this fixture key; irreversible. Use a fresh fixture for successful authentication.')
elif a.action in ('error-403','error-empty','policy-reset'):
    value=json.loads(inside(root,'control/original-policy.json').read_text())
    if a.action=='error-403': value['clients'][0]['addresses']=['192.0.2.0/24']
    if a.action=='error-empty': value['clients'][0]['grants']=[]
    atomic_bytes(root/'policy.json',json.dumps(value,indent=2).encode())
    record_control(root,a.action);print('Changed only the fixture access policy')
elif a.action in ('error-trust','trust-reset'):
    data=inside(root,'control/original-public.pem').read_bytes() if a.action=='trust-reset' else json.loads(inside(root,'device-public.json').read_text())['publicKeyPem'].encode()
    atomic_bytes(root/'public.pem',data)
    record_control(root,a.action);print('Changed only this fixture release verification public key')
elif a.action=='diagnostics':
    output=control/('diagnostics-'+a.name+'.zip')
    if output.exists(): raise ValueError('Use a new diagnostic artifact name')
    subprocess.run([str(exe),'diagnostics','export','--config',str(root/'client/general.json'),'--output',str(output)],env=env,check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    with zipfile.ZipFile(output) as archive:
        report=json.loads(archive.read('doctor.json'))
        if not report.get('supportId') or not report.get('preparationState'): raise ValueError('Missing structured readiness report')
        for item in archive.infolist():
            content=archive.read(item).decode('utf-8')
            if 'PRIVATE KEY' in content or re.search(r'Bearer\s+(?!<redacted>)\S+',content): raise ValueError('Support artifact contains a secret')
        if mode=='managed' and any(item.filename.startswith('state/') for item in archive.infolist()): raise ValueError('Managed client exported protected state')
    record_control(root,a.action,mode=mode)
    print('PASS: structured support ZIP, private-key/Bearer scan and managed state exclusion')
elif a.action=='verify':
    print('PASS: installed files='+str(verify_files(app,state/'installed-manifest.json')))
elif a.action=='verify-backup':
    backup=latest_backup(); meta=backup/'.uedt-meta'
    info=json.loads((meta/'backup-info.json').read_text())
    if info.get('previousVersion')!=a.version or info.get('newVersion')!=a.version or info.get('addedPaths'): raise ValueError('Not a normal same-version backup')
    print('PASS: backup='+backup.name+' files='+str(verify_files(backup,meta/'installed-manifest.json')))
elif a.action=='invalidate-preview':
    with mutation_guard(root,fixture,a.version):
        target=inside(root,str((latest_backup()/'.uedt-meta'/'backup-info.json').relative_to(root)))
        with target.open('a',encoding='utf-8') as stream: stream.write(' ')
    record_control(root,a.action,version=a.version,backupId=target.parent.parent.name)
    print('Changed fixture preview fingerprint only')
elif a.action.startswith('gui-'):
    profile=a.action[4:]
    selected=fixture['binaries'].get('developer',fixture['binaries']['launcher']) if profile=='developer' else fixture['binaries']['launcher']
    process=subprocess.Popen([selected['path'],'--gui','--config',str(root/'client'/(profile+'.json'))],env=env)
    print(json.dumps(dict(pid=process.pid,profile=profile)))
elif a.action in ('snapshot','compare'):
    assert re.fullmatch(r'[a-zA-Z0-9-]+',a.name)
    path=control/(a.name+'.snapshot.json')
    if a.action=='snapshot': path.write_text(json.dumps({'scope':a.scope,'files':snapshot()},indent=2)); print('SNAPSHOT '+str(path))
    else:
        record=json.loads(path.read_text())
        if 'scope' in record:
            if record['scope']!=a.scope:raise ValueError('Snapshot scope mismatch')
            record=record['files']
        elif a.scope!='all':raise ValueError('Legacy snapshot is all-scope only')
        assert record==snapshot(),'Protected file set/hash changed';print('PASS: protected snapshot unchanged')
elif a.action=='damage':
    with mutation_guard(root,fixture,a.version):
        target=inside(root,str((app/'version.txt').relative_to(root)))
        assert target.is_file(); target.write_text('fixture-damaged')
    record_control(root,a.action,version=a.version);print('Damaged synthetic version.txt only')
elif a.action=='release':
    if not a.attempt or not re.fullmatch(r'[a-f0-9]{32}',a.attempt): raise ValueError('An exact --attempt is required')
    marker=inside(control,a.attempt+'.started.json')
    if not marker.exists(): raise ValueError('Unknown synthetic attempt')
    if inside(control,a.attempt+'.ended.json').exists():raise ValueError('Synthetic attempt has already ended; no live lifetime evidence')
    if json.loads(marker.read_text()).get('version')!=a.version:raise ValueError('Synthetic attempt belongs to another version')
    observation=product_json(root,fixture,'runtime','inspect','--config',inside(root,'client/general.json'),'--version',a.version)
    if observation.get('state')!=2:raise ValueError('The exact selected runtime is not running')
    if json.loads(marker.read_text()).get('runtimeAttemptId')!=observation.get('attemptId'):raise ValueError('Synthetic marker belongs to an older runtime attempt')
    inside(control,'release-'+a.attempt).touch()
    deadline=time.monotonic()+15
    while not inside(control,a.attempt+'.ended.json').exists():
        if time.monotonic()>deadline: raise TimeoutError('Synthetic natural exit not confirmed')
        time.sleep(.1)
    record_control(root,a.action,attempt=a.attempt)
    ended=json.loads(inside(control,a.attempt+'.ended.json').read_text())
    if ended.get('reason')!='requested':raise ValueError('Synthetic watchdog expiry is not an explicit release')
    print('Confirmed exact requested child exit')
elif a.action=='status':
    print(json.dumps(dict(deploymentMode=mode,sourceHead=fixture.get('sourceHead'),revoked=(control/'auth-key-revoked').exists())))
    if mode=='managed':
        try:
            response=agent_status(env['UE_DT_AGENT_ENDPOINT'])
            print(json.dumps(dict(agentReady=response.get('success'),capabilities=response.get('agentCapabilities',[]))))
        except OSError: print('Agent not connected')
    for f in sorted(control.glob('*.started.json')): print(f.read_text()+' ended='+str(f.with_name(f.name.replace('.started.','.ended.')).exists()))
    for f in (root/'client'/'state').rglob('runtime-state.json'):
        value=json.loads(f.read_text()); print(json.dumps(dict(path=str(f.relative_to(root)),state=value['state'],attempt=value.get('attemptId'))))
elif a.action=='smoke':
    folder=root/'smoke'; folder.mkdir()
    entry='game.exe' if os.name=='nt' else 'game.sh'
    before=set(control.glob('*.started.json'))
    with zipfile.ZipFile(root/'server'/'incoming'/'1.0.0'/'Package.zip') as package:
        for name in (entry,'version.txt'): (folder/name).write_bytes(package.read(name))
    if sha256(folder/entry)!=fixture['binaries']['synthetic']['sha256'] or (folder/'version.txt').read_text().strip()!='1.0.0':
        raise ValueError('Smoke payload no longer matches the recorded synthetic binary')
    if os.name!='nt': (folder/entry).chmod(0o700)
    process=subprocess.Popen([str(folder/entry),'--control-root',str(control),'--smoke'],creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    assert process.wait(timeout=10)==0
    deadline=time.monotonic()+10
    while not (set(control.glob('*.started.json'))-before) and time.monotonic()<deadline: time.sleep(.05)
    marker=next(iter(set(control.glob('*.started.json'))-before)); value=json.loads(marker.read_text()); assert value['version']=='1.0.0'
    assert not marker.with_name(marker.name.replace('.started.','.ended.')).exists()
    (control/('release-'+value['id'])).touch()
    deadline=time.monotonic()+10
    while not marker.with_name(marker.name.replace('.started.','.ended.')).exists() and time.monotonic()<deadline: time.sleep(.05)
    assert marker.with_name(marker.name.replace('.started.','.ended.')).exists()
    assert not (root/'client'/'apps').exists(), 'GUI fixture must stay uninstalled'
    print('PASS: parent exits first, child persists and exits naturally; GUI install remains empty')
else:
    if a.action=='stop-all':
        for marker in control.glob('*.started.json'): (control/('release-'+marker.name.split('.')[0])).touch()
    acknowledgement=None
    if a.action.startswith(('stop-','start-')) and a.action!='stop-all':
        verb,kind=a.action.split('-');acknowledgement=control/(kind+('-stopped' if verb=='stop' else '-ready'))
        acknowledgement.unlink(missing_ok=True)
    (control/a.action).touch()
    if acknowledgement is not None:
        deadline=time.monotonic()+35
        while not acknowledgement.exists():
            if time.monotonic()>deadline: raise TimeoutError('Fixture harness did not acknowledge '+a.action)
            time.sleep(.1)
    print(a.action)
