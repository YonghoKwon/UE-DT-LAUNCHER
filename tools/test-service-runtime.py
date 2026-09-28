"""Synthetic published portable service: health failure and crash barriers. No OS service install."""
import argparse
import hashlib
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

p=argparse.ArgumentParser(); p.add_argument('--launcher',required=True); args=p.parse_args()
launcher=str(Path(args.launcher).resolve()); root=Path(tempfile.mkdtemp(prefix='uedt-service-proof-'))
platform='windows-x64' if os.name=='nt' else 'linux-x64'
entry='game.exe' if os.name=='nt' else 'game.sh'
web=root/'web'; web.mkdir()
if os.name=='nt':
    shutil.copy2(Path(os.environ['SystemRoot'])/'System32'/'cmd.exe',web/entry)
    arguments=['/c','type nul > started.txt & ping -n 5 127.0.0.1 > nul & type nul > ended.txt']
else:
    (web/entry).write_text('#!/bin/sh\ntouch started.txt\nsleep 4\ntouch ended.txt\n'); arguments=[]
health_mode='fail'; health_entered=threading.Event(); release_health=threading.Event()
class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self,*a,**kw): super().__init__(*a,directory=str(web),**kw)
    def log_message(self,*a): pass
    def do_GET(self):
        if self.path=='/health':
            health_entered.set()
            if health_mode=='hold': release_health.wait(15)
            if health_mode=='timeout': time.sleep(3)
            if health_mode=='disconnect': self.close_connection=True; return
            try: self.send_response(200 if health_mode=='ok' else 500); self.end_headers()
            except (BrokenPipeError,ConnectionResetError): pass
        else: super().do_GET()
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
url='http://127.0.0.1:'+str(server.server_port)
manifest=dict(appId='demo',version='1',platform=platform,entryPoint=entry,baseUrl=url+'/',files=[dict(path=entry,url=entry,size=(web/entry).stat().st_size,sha256=hashlib.sha256((web/entry).read_bytes()).hexdigest(),executable=True)])
(web/'manifest.json').write_text(json.dumps(manifest))
config=root/'config.json'
config.write_text(json.dumps(dict(schemaVersion=1,projectId='demo',targetPlatform=platform,manifestUrl=url+'/manifest.json',installDir=str(root/'app'),stateRootDir=str(root/'state'),logDir=str(root/'logs'),launchArguments=arguments,serviceMode=dict(autoRestartApp=True,startupGraceSeconds=0,healthCheckUrl=url+'/health',healthCheckTimeoutSeconds=2))))
snapshot=root/'state'/'service'/'demo'/'prod'/'stable'/platform/'service-state.json'
runtime=root/'state'/'demo'/platform/'runtime-state.json'
def run(*a):
    log=root/('command-'+str(time.time_ns())+'.log')
    with log.open('w') as output:
        result=subprocess.run([launcher,*a,'--config',str(config)],stdout=output,stderr=output,timeout=30)
    return subprocess.CompletedProcess(result.args,result.returncode,log.read_text(encoding='utf-8'), '')
def wait_stopped():
    deadline=time.monotonic()+15
    while time.monotonic()<deadline:
        if runtime.exists() and json.loads(runtime.read_text())['state']==0: return
        time.sleep(.05)
    raise AssertionError('No supervised completion')
child=None
try:
    failed=run('service','--once'); assert failed.returncode==1,failed.stdout+failed.stderr
    assert health_entered.is_set(),failed.stdout+failed.stderr
    assert json.loads(snapshot.read_text())['phase']==2
    deadline=time.monotonic()+2
    while not (root/'app'/'started.txt').exists() and time.monotonic()<deadline: time.sleep(.02)
    assert (root/'app'/'started.txt').exists() and not (root/'app'/'ended.txt').exists(), 'payload killed/failed'
    wait_stopped(); assert (root/'app'/'ended.txt').exists(), 'payload did not finish naturally'
    before=snapshot.read_bytes(); assert run('service','--once').returncode==1; assert before==snapshot.read_bytes()
    assert run('runtime','recover','--confirm-stopped','--service-selection','--version','1').returncode==0
    for mode in ('timeout','disconnect'):
        health_mode=mode; health_entered.clear()
        (root/'app'/'ended.txt').unlink()
        assert run('service','--once').returncode==1
        assert health_entered.is_set()
        assert json.loads(snapshot.read_text())['phase']==2
        wait_stopped(); assert (root/'app'/'ended.txt').exists(), 'health failure killed payload'
        assert run('runtime','recover','--confirm-stopped','--service-selection','--version','1').returncode==0
    health_mode='hold'; health_entered.clear()
    child=subprocess.Popen([launcher,'service','--once','--config',str(config)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    assert health_entered.wait(15),'health never reached'
    child.kill(); child.wait(timeout=5); release_health.set()
    assert json.loads(snapshot.read_text())['phase']==1,'crash lost startup barrier'
    wait_stopped(); assert run('service','--once').returncode==1
    assert run('runtime','recover','--confirm-stopped','--service-selection','--version','1').returncode==0
    health_mode='ok'; assert run('service','--once').returncode==0
    wait_stopped(); assert json.loads(snapshot.read_text())['phase']==0
    print('PASS: health failure, natural payload completion, service process crash, durable barrier and explicit recovery: '+str(root))
finally:
    release_health.set()
    if child and child.poll() is None: child.kill(); child.wait(timeout=5)
    server.shutdown(); server.server_close()
