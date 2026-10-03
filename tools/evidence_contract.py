"""Allowlisted summaries written atomically, including failures and timeouts."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import uuid


def provenance(binaries):
    result=subprocess.run(['git','rev-parse','HEAD'],capture_output=True,text=True)
    source=result.stdout.strip() if result.returncode==0 else 'unavailable'
    diff=subprocess.run(['git','diff','HEAD','--','src','tools','scripts','installer','packaging','Directory.Build.props'],capture_output=True).stdout
    untracked=subprocess.run(['git','ls-files','--others','--exclude-standard','--','src','tools','scripts','installer','packaging'],capture_output=True,text=True).stdout.splitlines()
    for name in sorted(untracked):
        path=Path(name)
        if path.is_file():diff+=name.encode()+b'\0'+path.read_bytes()
    hashes={}
    for role,path in binaries.items():
        with Path(path).open('rb') as stream:hashes[role]=hashlib.file_digest(stream,'sha256').hexdigest()
    return {'source':source if re.fullmatch('[0-9a-f]{40}',source) else 'unavailable','sourceDirty':bool(diff),
            'sourceDiffSha256':hashlib.sha256(diff).hexdigest(),'binarySha256':hashes}


def atomic(path,record):
    path=Path(path).absolute()
    if path.is_symlink() or getattr(path,'is_junction',lambda:False)():raise ValueError('Linked evidence output')
    for parent in path.parents:
        if parent.is_symlink() or getattr(parent,'is_junction',lambda:False)():raise ValueError('Linked evidence parent')
    path.parent.mkdir(parents=True,exist_ok=True)
    temporary=path.with_name('.'+path.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        with temporary.open('x',encoding='utf-8') as output:
            json.dump(record,output,indent=2);output.write('\n');output.flush();os.fsync(output.fileno())
        os.replace(temporary,path)
    finally:
        if temporary.exists():temporary.unlink()


class Evidence:
    def __init__(self,path,binaries,names):
        self.path=path
        self.record={'schemaVersion':2,**provenance(binaries),'platform':'Windows' if os.name=='nt' else 'Linux',
                     'complete':False,'success':False,'checks':[{'name':name,'status':'not-run','exitCode':None} for name in names]}
        self.save()
    def start(self,name):
        self.item(name)['status']='running';self.save()
    def item(self,name):return next(row for row in self.record['checks'] if row['name']==name)
    def finish(self,name,success,code=None,kind=None):
        item=self.item(name);item.update(status='passed' if success else 'failed',exitCode=code)
        if kind in ('timeout','process-failed','invalid-evidence','fixture-invalid'):item['failureType']=kind
        self.save()
    def save(self):
        self.record['counts']={status:sum(row['status']==status for row in self.record['checks']) for status in ('passed','failed','not-run','running')}
        atomic(self.path,self.record)
    def complete(self,success):
        self.record.update(complete=True,success=bool(success));self.save()
