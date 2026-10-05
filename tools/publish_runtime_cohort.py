"""Build all roles from one committed source snapshot and preserve publication failures."""
import argparse,hashlib,json,os,subprocess,tarfile,zipfile,shutil,inspect
from pathlib import Path
from gui_fixture_evidence import sha256,inside
from evidence_contract import atomic

REPO=Path(__file__).resolve().parents[1]
SUPPORT=('e_sqlite3.dll','libe_sqlite3.so','libe_sqlite3.dylib','UeDtLauncher.DistributionServer.deps.json','UeDtLauncher.DistributionServer.runtimeconfig.json')

def extract_snapshot(archive,destination):
    with tarfile.open(archive) as data:
        for member in data.getmembers():
            inside(destination,member.name)
            if member.issym() or member.islnk() or not (member.isfile() or member.isdir()):raise ValueError('Linked/non-file build inputs are not supported')
        if 'filter' in inspect.signature(data.extractall).parameters:data.extractall(destination,filter='data')
        else:data.extractall(destination) # older Python, same explicit containment/type checks above

def input_inventory(snapshot):
    candidates=list((snapshot/'src').rglob('*'))+list((snapshot/'tools/SyntheticGuiApp').rglob('*'))
    candidates += [snapshot/name for name in ('Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config')]
    return {str(p.relative_to(snapshot)).replace('\\','/'):sha256(p) for p in candidates if p.is_file() and not any(part in ('obj','bin') for part in p.relative_to(snapshot).parts)}

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--output',required=True)
    parser.add_argument('--rid',choices=['win-x64','linux-x64'],default='win-x64');parser.add_argument('--source-ref',default='HEAD')
    args=parser.parse_args();root=Path(args.output).resolve()
    if root.exists():raise ValueError('Use a new publication directory')
    if subprocess.check_output(['git','status','--porcelain','--','src','Directory.Build.props','Directory.Build.targets','global.json','NuGet.Config','tools/SyntheticGuiApp'],cwd=REPO):
        raise ValueError('Commit product build inputs before snapshot publication')
    head=subprocess.check_output(['git','rev-parse',args.source_ref+'^{commit}'],cwd=REPO,text=True).strip()
    root.mkdir(parents=True);record={'schemaVersion':2,'sourceHead':head,'sourceSnapshot':True,'productSourceDirty':False,'rid':args.rid,'success':False,'phase':'snapshot'}
    atomic(root/'publication-status.json',record)
    try:
        archive=root/'source.tar';snapshot=root/'source';snapshot.mkdir()
        subprocess.run(['git','archive','--format=tar','--output='+str(archive),head],cwd=REPO,check=True)
        extract_snapshot(archive,snapshot);inputs=input_inventory(snapshot)
        record['productSourceHash']=hashlib.sha256(json.dumps(inputs,sort_keys=True).encode()).hexdigest()
        record['sdkVersion']=subprocess.check_output(['dotnet','--version'],cwd=snapshot,text=True).strip()
        suffix='.exe' if args.rid=='win-x64' else '';binaries={};assets={}
        roles=[('launcher','UeDtLauncher','src/UeDtLauncher'),('developer','UeDtLauncher','src/UeDtLauncher'),('agent','UeDtLauncher.Agent','src/UeDtLauncher.Agent'),('server','UeDtLauncher.DistributionServer','src/UeDtLauncher.DistributionServer'),('synthetic','SyntheticGuiApp','tools/SyntheticGuiApp')]
        for role,project,relative in roles:
            record['phase']=role;atomic(root/'publication-status.json',record);output=root/role
            result=subprocess.run(['dotnet','publish',str(snapshot/relative/(project+'.csproj')),'-c','Release','-r',args.rid,'--self-contained','true',
                '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:LauncherEdition='+('Developer' if role=='developer' else 'General'),'-o',str(output)],
                cwd=snapshot,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=300)
            (root/(role+'-publish.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
            if result.returncode:raise RuntimeError('Publication failed; private build log retained')
            if 'warning ' in result.stdout.lower() or 'warning ' in result.stderr.lower():raise RuntimeError('Publication warnings require review')
            executable=output/(('UeDtLauncher.Developer' if role=='developer' else project)+suffix)
            artifact={'path':str(executable),'sha256':sha256(executable),'edition':('Developer' if role=='developer' else 'General') if role in ('launcher','developer') else None}
            if role=='synthetic':assets[role]=artifact
            else:binaries[role]=artifact
            if role in ('launcher','developer') and (os.name=='nt')==(args.rid=='win-x64'):
                info=json.loads(subprocess.check_output([str(executable),'--build-info'],text=True,encoding='utf-8'))
                if info['edition']!=artifact['edition']:raise ValueError('Compiled edition mismatch')
                artifact['buildInfo']=info
        shutil.copy2(binaries['launcher']['path'],root/'developer'/('UeDtLauncher'+suffix))
        support={}
        for name in SUPPORT:
            path=root/'server'/name
            if path.exists():support['server/'+name]={'path':str(path),'sha256':sha256(path)}
        packages={}
        for edition,roles in [('General',['launcher']),('Developer',['launcher','developer'])]:
            path=root/('UeDtLauncher-'+edition+'-'+args.rid+'-UNSIGNED-DEV.zip')
            with zipfile.ZipFile(path,'w',zipfile.ZIP_DEFLATED) as package:
                for role in roles:package.write(binaries[role]['path'],Path(binaries[role]['path']).name)
            packages[edition]={'path':str(path),'sha256':sha256(path)}
        if inputs!=input_inventory(snapshot):raise ValueError('Frozen product build inputs changed')
        record.update(success=True,phase='complete',binaries=binaries,fixtureAssets=assets,supportFiles=support,packages=packages)
        atomic(root/'cohort.json',record);atomic(root/'publication-status.json',record)
        print(json.dumps({'sourceHead':head,'productSourceHash':record['productSourceHash'],'rid':args.rid,'success':True,'manifest':str(root/'cohort.json')}))
    except Exception as error:
        record.update(success=False,failureType='timeout' if isinstance(error,subprocess.TimeoutExpired) else type(error).__name__)
        atomic(root/'publication-status.json',record);raise

if __name__=='__main__':main()
