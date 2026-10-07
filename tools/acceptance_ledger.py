"""Scenario-level evidence for one published GUI cohort. This module never clicks or installs."""
import json,re
import hashlib,os
from datetime import datetime
from pathlib import Path
from evidence_contract import atomic
from gui_fixture_evidence import inside,sha256

SCENARIOS={'initial-configuration','connection-errors','empty-and-promotion','v1-install-lifetime','v2-cancel-resume-launch',
           'runtime-protection','repair-followup','normal-backup','restore-confirmation','restore-followup','authentication-errors','diagnostics'}
DEVELOPER={'exact-confirmation','maintenance-outcomes'}
CHECKS={
 'initial-configuration':{'missing-help','invalid-config-retry-readonly','edition-fixed'},
 'connection-errors':{'server-reconnect','retry-no-mutation'},
 'empty-and-promotion':{'empty-cleared','promotion-wait','promotion-refresh-readonly'},
 'v1-install-lifetime':{'gui-install','all-file-hashes','child-survives-gui-close','requested-end'},
 'v2-cancel-resume-launch':{'received-bytes','safe-cancel','immediate-resume','range-request','all-file-hashes','v1-preserved','no-auto-launch','explicit-launch'},
 'runtime-protection':{'alive-before','alive-after','changes-blocked','protected-unchanged'},
 'repair-followup':{'normal-query-only','missing-install-guidance','damaged-repaired','post-commit-query-failure','buttons-query-only'},
 'normal-backup':{'healthy-extra-repair','all-backup-hashes'},
 'restore-confirmation':{'cancel-unchanged','changed-preview-rejected','new-confirmation','normal-restored'},
 'restore-followup':{'restore-file-witness','query-only-retry','no-second-restore'},
 'authentication-errors':{'unauthorized','forbidden','integrity-failed','support-id','no-secret'},
 'diagnostics':{'zip-created','secret-scan'},
 'exact-confirmation':{'v1-exact','v2-exact','cancel-unchanged','confirmed-selection'},
 'maintenance-outcomes':{'staging-only','resume-preserved','partial-failure-reported','cleanup-only-retry'}
}

def load_proof(root,reference):
    path=inside(root,reference)
    with path.open('rb') as stream:data=stream.read(1024*1024+1)
    if len(data)>1024*1024:raise ValueError('Oversized acceptance proof')
    value=json.loads(data.decode('utf-8-sig'))
    if not isinstance(value,dict):raise ValueError('Acceptance proof must be a JSON object')
    return value

def actual_display(root,profile):
    run=load_proof(root,'control/gui-run-'+profile+'.json')
    started=datetime.fromisoformat(run['startedUtc'].replace('Z','+00:00'))
    config=inside(root,'client/'+profile+'.json')
    directories=[inside(root,'client/logs')]
    if os.name=='nt':
        identity=hashlib.sha256(str(config).encode()).hexdigest()[:16]
        directories.append(Path(os.environ['LOCALAPPDATA'])/'UE-DT Launcher/ui-logs'/identity)
    candidates=[]
    for directory in directories:
        for path in directory.glob('*.log'):
            if path.is_symlink():raise ValueError('Linked display log')
            with path.open('rb') as stream:
                stream.seek(max(0,path.stat().st_size-512*1024))
                lines=stream.read().decode('utf-8',errors='replace').splitlines()
            for index,line in enumerate(lines):
                if '[UiDisplay]' not in line:continue
                try:value=json.loads(line.split('[UiDisplay]',1)[1].strip())
                except ValueError:continue
                try:session_start=datetime.fromisoformat(value['sessionStartedUtc'].replace('Z','+00:00'))
                except (KeyError,TypeError,ValueError):continue
                if value.get('profile')==profile and value.get('processId')==run['pid'] and session_start>=started:
                    candidates.append((path.stat().st_mtime_ns,index,value))
    if not candidates:raise ValueError('Actual GUI display diagnostic is missing')
    value=max(candidates,key=lambda row:(row[0],row[1]))[2]
    pixels=[int(v.strip()) for v in value['screenPixels'].split(',')][-2:]
    return {'pixels':pixels,'renderScaling':value['renderScaling'],'textScale':value['textScale'],'highContrast':value['highContrast'],
            'runId':run['runId'],'sessionId':value['sessionId']}

def create(root,fixture):
    publication=fixture.get('productPublication')
    if publication is None:raise ValueError('Acceptance requires a successful snapshot cohort')
    path=inside(root,'control/acceptance-ledger.json')
    if path.exists():raise ValueError('Existing acceptance evidence is preserved')
    record={'schemaVersion':2,'fixtureId':fixture['id'],'productSourceHash':publication['productSourceHash'],
            'binarySha256':publication['binarySha256'],'cases':[],'complete':False}
    for profile in ('general','developer'):
        names=SCENARIOS|DEVELOPER if profile=='developer' else SCENARIOS
        if profile=='developer' and fixture['deploymentMode']=='managed':names=names-{'maintenance-outcomes'}
        for name in sorted(names):record['cases'].append({'profile':profile,'name':name,'status':'not-run'})
    atomic(path,record);return record

def load(root,fixture):
    path=inside(root,'control/acceptance-ledger.json')
    if not path.exists():return create(root,fixture)
    record=load_proof(root,'control/acceptance-ledger.json')
    if record.get('fixtureId')!=fixture['id'] or record.get('productSourceHash')!=fixture['productPublication']['productSourceHash']:
        raise ValueError('Acceptance evidence belongs to another cohort')
    return record

def record_case(root,fixture,profile,name,status,proof=None):
    record=load(root,fixture);row=next((c for c in record['cases'] if c['profile']==profile and c['name']==name),None)
    if row is None or status not in ('running','passed','failed','blocked','not-run'):raise ValueError('Invalid acceptance case')
    if status=='passed':
        if record.get('schemaVersion')!=2:raise ValueError('Historical evidence cannot receive new acceptance passes')
        if not isinstance(proof,dict) or proof.get('inputActor') not in ('computer-use','user'):raise ValueError('CLI/headless execution cannot pass a GUI case')
        if proof.get('productSourceHash')!=record['productSourceHash'] or proof.get('profile')!=profile:raise ValueError('Case source/profile mismatch')
        if proof.get('fixtureId')!=fixture['id']:raise ValueError('Case fixture mismatch')
        display=actual_display(root,profile)
        if proof.get('screen')!=display or {k:display[k] for k in ('pixels','renderScaling','textScale','highContrast')}!={'pixels':[1920,1080],'renderScaling':1,'textScale':1,'highContrast':False}:raise ValueError('Actual current-run screen evidence is required')
        if proof.get('result') is not True or not proof.get('checks') or not all(v is True for v in proof['checks'].values()):raise ValueError('Incomplete case checks')
        required=CHECKS[name]|({'service-reconnect'} if name=='connection-errors' and fixture['deploymentMode']=='managed' else set())
        if not required<=set(proof['checks']):raise ValueError('Required scenario checks are missing')
        for label in proof['checks']:
            if not re.fullmatch('[a-z0-9-]{1,80}',label):raise ValueError('Unsafe evidence label')
        screenshots=proof.get('screenshots')
        if not isinstance(screenshots,list) or not screenshots:raise ValueError('A GUI screenshot is required')
        for capture_name in screenshots:
            path=inside(root,capture_name)
            with path.open('rb') as stream:
                signature=stream.read(8)
                if signature!=b'\x89PNG\r\n\x1a\n' and not signature.startswith(b'\xff\xd8\xff'):raise ValueError('A native PNG/JPEG capture is required')
        row['screenshots']=[{'name':Path(p).name,'sha256':sha256(inside(root,p))} for p in screenshots]
        row['checks']=proof['checks'];row['inputActor']=proof['inputActor'];row['screen']=proof['screen']
        if 'snapshotScope' in proof:
            if proof['snapshotScope'] not in ('all','protected'):raise ValueError('Invalid comparison scope')
            if proof.get('beforeScope')!=proof['snapshotScope'] or proof.get('afterScope')!=proof['snapshotScope']:raise ValueError('Mismatched snapshot scope')
            row['snapshotScope']=proof['snapshotScope']
        if proof.get('runtimeEndReason') not in (None,'requested'):raise ValueError('Watchdog exit is not requested lifetime completion')
        if proof.get('guiInstall') is False:raise ValueError('CLI installation cannot pass GUI installation')
        if name=='v1-install-lifetime' and (proof.get('guiInstall') is not True or proof.get('runtimeEndReason')!='requested' or not re.fullmatch('[a-f0-9]{32}',proof.get('runtimeAttemptId',''))):
            raise ValueError('Actual GUI installation and exact requested runtime completion are mandatory')
        for field in ('guiInstall','runtimeEndReason','runtimeAttemptId'):
            if field in proof:row[field]=proof[field]
    row['status']=status;record['complete']=all(c['status']=='passed' for c in record['cases'])
    atomic(inside(root,'control/acceptance-ledger.json'),record);return row

def summary(root,fixture,profile):
    record=load(root,fixture);cases=[c for c in record['cases'] if c['profile']==profile]
    return {'schemaVersion':1,'mode':fixture['deploymentMode'],'profile':profile,'productSourceHash':record['productSourceHash'],
            'binarySha256':record['binarySha256'],'complete':all(c['status']=='passed' for c in cases),
            'counts':{s:sum(c['status']==s for c in cases) for s in ('passed','failed','not-run','running','blocked')},'cases':cases}

def aggregate(summaries):
    required={(mode,profile) for mode in ('managed','portable') for profile in ('general','developer')}
    if {(r['mode'],r['profile']) for r in summaries}!=required or len(summaries)!=4:raise ValueError('Four distinct client combinations are required')
    if len({r['productSourceHash'] for r in summaries})!=1 or len({json.dumps(r['binarySha256'],sort_keys=True) for r in summaries})!=1:
        raise ValueError('Different final publications cannot be combined')
    return {'complete':all(r['complete'] for r in summaries),'productSourceHash':summaries[0]['productSourceHash'],'combinations':summaries}
