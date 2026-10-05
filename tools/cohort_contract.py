"""Local publication provenance and role membership; not a remote signature format."""
import json,re
from pathlib import Path
from gui_fixture_evidence import sha256,inside

ROLES={'launcher':'General','developer':'Developer','agent':None,'server':None}
def read_publication(path):
    path=Path(path).resolve()
    if path.is_symlink() or path.stat().st_size>1024*1024:raise ValueError('Invalid publication manifest')
    value=json.loads(path.read_text())
    if value.get('schemaVersion')!=2 or value.get('success') is not True or value.get('rid') not in ('win-x64','linux-x64'):
        raise ValueError('A successful snapshot publication is required')
    for name,size in [('sourceHead',40),('productSourceHash',64)]:
        if not re.fullmatch('[a-f0-9]{'+str(size)+'}',value.get(name,'')):raise ValueError('Invalid product provenance')
    if set(value.get('binaries',{}))!=set(ROLES):raise ValueError('Incomplete publication roles')
    for role,edition in ROLES.items():
        artifact=value['binaries'][role]
        if artifact.get('edition')!=edition:raise ValueError('Wrong publication edition')
        verify_artifact(path.parent,artifact)
    verify_artifact(path.parent,value.get('fixtureAssets',{}).get('synthetic',{}))
    for artifact in value.get('supportFiles',{}).values():verify_artifact(path.parent,artifact)
    return value

def verify_artifact(root,value):
    path=Path(value.get('path','')).resolve()
    inside(root,path.relative_to(root.resolve()))
    if not path.is_file() or path.is_symlink() or sha256(path)!=value.get('sha256'):
        raise ValueError('Publication artifact missing or changed')

def validate_inputs(manifest,binaries):
    publication=read_publication(manifest)
    expected=dict(publication['binaries'],synthetic=publication['fixtureAssets']['synthetic'])
    for role,path in binaries.items():
        value=expected.get(role)
        if value is None or Path(path).resolve()!=Path(value['path']).resolve() or sha256(path)!=value['sha256']:
            raise ValueError('Fixture input belongs to another publication')
    return publication

def fixture_provenance(publication):
    return {'sourceHead':publication['sourceHead'],'sourceSnapshot':True,'productSourceHash':publication['productSourceHash'],
            'rid':publication['rid'],'sdkVersion':publication.get('sdkVersion'),
            'binarySha256':{k:v['sha256'] for k,v in publication['binaries'].items()},
            'supportSha256':{k:v['sha256'] for k,v in publication.get('supportFiles',{}).items()},
            'syntheticSha256':publication['fixtureAssets']['synthetic']['sha256']}

def verify_fixture_publication(fixture):
    provenance=fixture.get('productPublication')
    if provenance is None:return # historical roots remain readable, not final acceptance evidence
    expected=provenance['binarySha256']
    for role,value in fixture['binaries'].items():
        digest=provenance['syntheticSha256'] if role=='synthetic' else expected.get(role)
        if value['sha256']!=digest:raise ValueError('Fixture role hash differs from its publication')
    for role,value in fixture.get('supportFiles',{}).items():
        if value['sha256']!=provenance['supportSha256'].get(role):raise ValueError('Runtime sidecar differs from publication')
    if set(fixture.get('supportFiles',{}))!=set(provenance['supportSha256']):raise ValueError('Missing runtime sidecar')
