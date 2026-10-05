"""Test-only mutation guards and redacted preflight. Uses the product's read-only runtime validation."""
import contextlib, ctypes, json, os, re, subprocess
from pathlib import Path
from gui_fixture_evidence import inside, fixture_environment, fixture_mode

def product_json(root,fixture,*arguments):
    result=subprocess.run([fixture['binaries']['launcher']['path'],*map(str,arguments)],env=fixture_environment(root,fixture),capture_output=True,text=True,encoding='utf-8',timeout=20,
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    if result.returncode:raise ValueError('Fixture product observation failed; no test file was changed')
    return json.loads(result.stdout)

def state_root(root,version,platform):
    return inside(root,'client/state/demo/prod/stable/'+version+'/'+platform)

def inspect_operations(root,fixture):
    folder=inside(root,'client/state/operations')
    records=sorted(folder.glob('*.json')) if folder.exists() else []
    if len(records)>256:raise ValueError('Too many fixture operations to verify')
    phases=[]
    for path in records:
        if not re.fullmatch(r'[a-f0-9]{32}',path.stem):raise ValueError('Unexpected fixture operation')
        value=product_json(root,fixture,'operation','status','--id',path.stem,'--config',inside(root,'client/general.json'))
        if fixture_mode(fixture)=='managed':
            if not value.get('success') or not value.get('operation'):raise ValueError('Operation observation unavailable')
            value=value['operation']
        phases.append(value['phase'])
    return phases

def require_quiescent(root,fixture,version):
    platform=fixture.get('platform','windows-x64' if os.name=='nt' else 'linux-x64')
    state=state_root(root,version,platform)
    if not inside(root,str((state/'runtime-state.json').relative_to(root))).is_file():raise ValueError('An explicit stopped runtime record is required')
    value=product_json(root,fixture,'runtime','inspect','--config',inside(root,'client/general.json'),'--version',version)
    if value.get('state')!=0:raise ValueError('Fixture runtime is not confirmed quiescent')
    if any(p not in ('Completed','Cancelled','Failed','Discarded','Interrupted') for p in inspect_operations(root,fixture)):
        raise ValueError('A fixture operation is active')
    metrics=inside(root,'control/proxy-metrics.json')
    if metrics.exists() and json.loads(metrics.read_text()).get('activeFiles',0)!=0:raise ValueError('A fixture transfer is active')

@contextlib.contextmanager
def mutation_guard(root,fixture,version):
    platform=fixture.get('platform','windows-x64' if os.name=='nt' else 'linux-x64')
    path=inside(root,str((state_root(root,version,platform)/'update.lock').relative_to(root)))
    if not path.is_file():raise ValueError('Existing installation lock is required')
    if os.name=='nt':
        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
        kernel.CreateFileW.argtypes=[ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
        kernel.CreateFileW.restype=ctypes.c_void_p;kernel.CloseHandle.argtypes=[ctypes.c_void_p]
        handle=kernel.CreateFileW(str(path),0xC0000000,0,None,3,0,None) # existing file, exclusive sharing; never create a lock
        if handle==ctypes.c_void_p(-1).value:raise ValueError('Installation mutation is active')
        try:require_quiescent(root,fixture,version);yield
        finally:kernel.CloseHandle(handle)
    else:
        import fcntl
        with path.open('r+b') as stream:
            try:fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
            except OSError as error:raise ValueError('Installation mutation is active') from error
            require_quiescent(root,fixture,version);yield

def preflight(root,fixture,version):
    control=inside(root,'control')
    faults=[name for name in ('proxy-unthrottle','proxy-fail-version','proxy-fail-catalog-after-commit','auth-key-revoked') if (control/name).exists()]
    original=control/'original-policy.json';policy=inside(root,'policy.json')
    if original.exists() and json.loads(original.read_text())!=json.loads(policy.read_text()):faults.append('policy-changed')
    if (control/'original-public.pem').exists() and (control/'original-public.pem').read_bytes()!=inside(root,'public.pem').read_bytes():faults.append('trust-changed')
    config_states={}
    for profile in ('general','developer'):
        try:
            config=json.loads(inside(root,'client/'+profile+'.json').read_text())
            config_states[profile]={'validJson':True,'resumeCacheBytes':config.get('performance',{}).get('resumeCacheBytes',0)}
        except (OSError,ValueError):config_states[profile]={'validJson':False}
    result={'cohortHashes':'verified','mode':fixture_mode(fixture),'version':version,'faults':faults,'configStates':config_states,'authentication':'not-checked'}
    try:
        value=product_json(root,fixture,'runtime','inspect','--config',inside(root,'client/general.json'),'--version',version)
        result['runtimeState']=value.get('state');result['operationPhases']=inspect_operations(root,fixture)
    except (ValueError,OSError,subprocess.SubprocessError):result['runtimeState']='unverified'
    return result
