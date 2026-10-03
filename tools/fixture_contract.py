"""Ownership/provenance for private synthetic fixtures; names never confer authority."""
import csv
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import uuid
from stream_hash import sha256_stream


def private_windows_acl(path):
    import ctypes
    from ctypes import wintypes as w
    api=ctypes.WinDLL('advapi32',use_last_error=True);kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    api.GetNamedSecurityInfoW.argtypes=[w.LPWSTR,w.DWORD,w.DWORD,ctypes.POINTER(ctypes.c_void_p),ctypes.c_void_p,ctypes.POINTER(ctypes.c_void_p),ctypes.c_void_p,ctypes.POINTER(ctypes.c_void_p)]
    api.GetNamedSecurityInfoW.restype=w.DWORD
    api.ConvertSidToStringSidW.argtypes=[ctypes.c_void_p,ctypes.POINTER(w.LPWSTR)]
    api.GetSecurityDescriptorControl.argtypes=[ctypes.c_void_p,ctypes.POINTER(w.WORD),ctypes.POINTER(w.DWORD)]
    api.GetAce.argtypes=[ctypes.c_void_p,w.DWORD,ctypes.POINTER(ctypes.c_void_p)]
    kernel.LocalFree.argtypes=[ctypes.c_void_p]
    sid=ctypes.c_void_p();acl=ctypes.c_void_p();descriptor=ctypes.c_void_p()
    code=api.GetNamedSecurityInfoW(str(path),1,5,ctypes.byref(sid),None,ctypes.byref(acl),None,ctypes.byref(descriptor))
    if code:raise ValueError('Cannot inspect fixture ACL')
    def sid_text(value):
        output=w.LPWSTR()
        if not api.ConvertSidToStringSidW(value,ctypes.byref(output)):raise ValueError('Cannot inspect fixture SID')
        try:return output.value
        finally:kernel.LocalFree(ctypes.cast(output,ctypes.c_void_p))
    try:
        control=w.WORD();revision=w.DWORD()
        if not api.GetSecurityDescriptorControl(descriptor,ctypes.byref(control),ctypes.byref(revision)) or not control.value&0x1000 or not acl.value:
            raise ValueError('Unsafe fixture DACL')
        current=owner()
        if sid_text(sid)!=current:raise ValueError('Wrong fixture owner')
        count=ctypes.c_ushort.from_address(acl.value+4).value
        if count>16:raise ValueError('Unexpected fixture ACE count')
        for index in range(count):
            ace=ctypes.c_void_p()
            if not api.GetAce(acl,index,ctypes.byref(ace)):raise ValueError('Cannot inspect ACE')
            if ctypes.c_ubyte.from_address(ace.value).value!=0 or sid_text(ctypes.c_void_p(ace.value+8)) not in (current,'S-1-5-18','S-1-5-32-544'):
                raise ValueError('Unsafe fixture access rule')
    finally:kernel.LocalFree(descriptor)


def owner():
    if os.name!='nt':return 'uid:'+str(os.getuid())
    text=subprocess.check_output(['whoami','/user','/fo','csv','/nh'],text=True,creationflags=subprocess.CREATE_NO_WINDOW)
    return next(csv.reader([text.strip()]))[-1]


def unlinked(path):
    path=Path(path).absolute()
    for component in [path,*path.parents]:
        if component.exists() and (component.is_symlink() or getattr(component,'is_junction',lambda:False)()):
            raise ValueError('Linked fixture path')
    return path


def inside(root,path):
    root=unlinked(root);path=unlinked(path)
    if not path.is_relative_to(root) or path==root:raise ValueError('Fixture path escapes private root')
    return path


def digest(path):
    with Path(path).open('rb') as stream:return sha256_stream(stream)


def claim(root):
    root=unlinked(root)
    if root.exists() and any(root.iterdir()):raise ValueError('Fixture must be new and empty')
    root.mkdir(parents=True,exist_ok=True)
    if os.name!='nt':root.chmod(0o700)
    else:
        sid=owner()
        subprocess.run(['icacls.exe',str(root),'/inheritance:r','/grant:r',
                        '*'+sid+':(OI)(CI)F','*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F'],
                       check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    record={'schemaVersion':1,'id':uuid.uuid4().hex,'owner':owner(),'root':str(root)}
    (root/'.headless-owner.json').write_text(json.dumps(record),encoding='utf-8')
    return root


def seal(root,binaries,config):
    root=unlinked(root)
    record=json.loads((root/'.headless-owner.json').read_text())
    record['configSha256']=digest(inside(root,config))
    record['binaries']={kind:{'path':str(unlinked(path)),'sha256':digest(path)} for kind,path in binaries.items()}
    (root/'.headless-owner.json').write_text(json.dumps(record),encoding='utf-8')


def verify(root,config,server):
    root=unlinked(root);marker=inside(root,root/'.headless-owner.json')
    if marker.stat().st_size>65536:raise ValueError('Fixture marker exceeds limit')
    record=json.loads(marker.read_text())
    if record.get('schemaVersion')!=1 or record.get('owner')!=owner() or record.get('root')!=str(root):raise ValueError('Fixture ownership mismatch')
    if os.name!='nt' and (root.stat().st_uid!=os.getuid() or stat.S_IMODE(root.stat().st_mode)&0o077):raise ValueError('Unsafe fixture mode/owner')
    if os.name=='nt':
        private_windows_acl(root)
    if digest(inside(root,config))!=record.get('configSha256'):raise ValueError('Fixture config changed')
    binary=record.get('binaries',{}).get('server',{})
    if str(unlinked(server))!=binary.get('path') or digest(server)!=binary.get('sha256'):raise ValueError('Wrong server cohort')
    settings=json.loads(Path(config).read_text(encoding='utf-8-sig'))
    for field in ('root','policyPath','signingKeyPath'):inside(root,settings[field])
    inside(root,Path(settings['root'])/'distribution.db')
    return settings
