"""An Agent service request must use requested A, never default B. No service installation."""
import argparse, hashlib, http.server, json, os, socket, struct, subprocess, tempfile, threading, time, uuid
from pathlib import Path
p=argparse.ArgumentParser(); p.add_argument('--agent',required=True); a=p.parse_args()
root=Path(tempfile.mkdtemp(prefix='uedt-service-target-')); platform='windows-x64' if os.name=='nt' else 'linux-x64'
web=root/'web'; web.mkdir(); (web/'game').write_bytes(b'fixture')
class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self,*a,**kw): super().__init__(*a,directory=str(web),**kw)
    def log_message(self,*a): pass
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler); threading.Thread(target=server.serve_forever,daemon=True).start()
url='http://127.0.0.1:'+str(server.server_port)
(web/'manifest.json').write_text(json.dumps(dict(appId='requested-a',version='1',platform=platform,entryPoint='game',baseUrl=url+'/',files=[dict(path='game',url='game',size=7,sha256=hashlib.sha256(b'fixture').hexdigest())])))
(root/'config').mkdir()
(root/'config'/'launcher.config.json').write_text(json.dumps(dict(schemaVersion=1,deploymentMode='managed-agent',projectId='default-b',targetPlatform=platform,manifestUrl=url+'/manifest.json',installDir=str(root/'app-b'),stateRootDir=str(root/'state'),logDir=str(root/'logs'),projects=[dict(projectId='requested-a',displayName='A',installPath=str(root/'app-a'))],serviceMode=dict(autoRestartApp=False))))
endpoint='uedt-target-'+uuid.uuid4().hex if os.name=='nt' else str(root/'agent.sock')
env=dict(os.environ,UE_DT_AGENT_DATA_ROOT=str(root),UE_DT_AGENT_ENDPOINT=endpoint)
with (root/'agent.log').open('w') as log:
    proc=subprocess.Popen([str(Path(a.agent).resolve())],env=env,stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0)
    try:
        deadline=time.monotonic()+15
        while True:
            try:
                if os.name=='nt': connection=open('\\\\.\\pipe\\'+endpoint,'r+b',buffering=0)
                else:
                    channel=socket.socket(socket.AF_UNIX); channel.settimeout(15); channel.connect(endpoint); connection=channel.makefile('rwb',buffering=0)
                break
            except OSError:
                if time.monotonic()>deadline: raise
                time.sleep(.05)
        request=dict(protocolVersion=1,correlationId=uuid.uuid4().hex,command='service-run',projectId='requested-a',clientCapabilities=['runtime-supervision-v1'])
        payload=json.dumps(request).encode(); connection.write(struct.pack('<i',len(payload))+payload)
        length=struct.unpack('<i',connection.read(4))[0]; body=bytearray()
        while len(body)<length:
            chunk=connection.read(length-len(body)); assert chunk; body.extend(chunk)
        response=json.loads(body); connection.close()
        if os.name!='nt': channel.close()
        assert response['success'],response
        assert (root/'app-a'/'game').read_bytes()==b'fixture'
        assert not (root/'app-b').exists()
        assert not (root/'state'/'default-b').exists()
        print('PASS: requested project A installed; default project B untouched: '+str(root))
    finally:
        proc.terminate(); proc.wait(timeout=10); server.shutdown(); server.server_close()
