"""Isolated HTTPS/Bearer + signed legacy manifest; never relax product launch policy."""
import http.server
import json
import os
from pathlib import Path
import secrets
import subprocess
import threading
from benchmark_tls_support import context


def configure(root,launcher,web,manifest,env,config,name='fixture'):
    token=secrets.token_hex(32)
    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self,*args,**kwargs):super().__init__(*args,directory=str(web),**kwargs)
        def log_message(self,*_):pass
        def do_GET(self):
            if self.headers.get('Authorization')!='Bearer '+token:
                self.send_response(401);self.send_header('Content-Length','0');self.end_headers();return
            super().do_GET()
    server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
    server.socket=context(root).wrap_socket(server.socket,server_side=True)
    url='https://127.0.0.1:'+str(server.server_port)
    manifest=dict(manifest,baseUrl=url+'/')
    (web/'manifest.json').write_text(json.dumps(manifest),encoding='utf-8')
    flags=subprocess.CREATE_NO_WINDOW if os.name=='nt' else 0
    def run(*args,variables=None):
        outcome=subprocess.run([str(launcher),*map(str,args)],env=variables or env,capture_output=True,timeout=30,creationflags=flags)
        if outcome.returncode:raise RuntimeError('Synthetic signed legacy provisioning failed')
    run('generate-signing-key','--private-key',root/'release-private.pem','--public-key',root/'release-public.pem')
    run('sign-manifest','--manifest',web/'manifest.json','--private-key',root/'release-private.pem','--key-id','fixture','--output',web/'manifest.json.sig')
    run('credential','set','--name',name,variables={**env,'UE_DT_CREDENTIAL_TOKEN':token})
    config.update(schemaVersion=2,manifestUrl=url+'/manifest.json',manifestSignatureUrl=url+'/manifest.json.sig',requireSignedManifests=True,
        security={'requireHttps':True,'authenticationMode':'bearer','credentialName':name,'customCaCertificatePath':str(root/'tls.pem'),
                  'allowedDownloadHosts':['127.0.0.1'],'trustedSigningKeys':[{'keyId':'fixture','publicKeyPath':str(root/'release-public.pem')}]})
    threading.Thread(target=server.serve_forever,daemon=True).start()
    return server,manifest
