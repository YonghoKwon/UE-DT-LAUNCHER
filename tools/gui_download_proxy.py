"""Test-only bounded loopback forwarding with observable synthetic file throttling."""
import http.client
import http.server
import json
from pathlib import Path
import threading
import time
import urllib.parse
import uuid
from gui_fixture_evidence import inside


class DownloadProxy:
    def __init__(self, root, public_port, upstream_port, bytes_per_second=512*1024):
        self.root=Path(root).resolve()
        self.control=self.root/'control';self.control.mkdir(exist_ok=True)
        self.upstream=upstream_port;self.rate=bytes_per_second
        self.lock=threading.Lock();self.slots=threading.BoundedSemaphore(16)
        self.stop=threading.Event()
        self.last_persist=0.0
        self.metrics=dict(sessionId=uuid.uuid4().hex,startedAtUtc=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),fileRequests=0,rangeRequests=0,rangeStarts=[],fileBytes=0,throttledBytes=0,activeFiles=0,configuredBytesPerSecond=bytes_per_second,catalogFaults=0)
        owner=self
        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version='HTTP/1.1'
            def log_message(self,*_):pass
            def handle(self):
                try:super().handle()
                except OSError:pass # A client cancellation can abort the next keep-alive read.
            def forward(self):
                if not owner.slots.acquire(blocking=False):
                    self.send_response(429);self.send_header('Content-Length','0');self.end_headers();return
                connection=None;file_request=False;registered=False
                try:
                    path=urllib.parse.urlsplit(self.path).path
                    if self.command not in ('GET','HEAD') or not (path in ('/api/v1/catalog','/api/v1/auth/challenge') or path.startswith('/releases/')):
                        self.send_error(404);return
                    file_request='/files/' in path
                    headers={k:v for k,v in self.headers.items() if k.lower() not in ('host','connection','x-forwarded-for','x-distribution-client-ip')}
                    headers['X-Distribution-Client-IP']=self.client_address[0]
                    connection=http.client.HTTPConnection('127.0.0.1',owner.upstream,timeout=30)
                    connection.request(self.command,self.path,headers=headers)
                    response=connection.getresponse()
                    if path=='/api/v1/catalog' and response.status==200 and owner.fail_catalog_after_commit():
                        self.send_response(503);self.send_header('Content-Length','0');self.end_headers();self.close_connection=True;return
                    if path.endswith('/files/version.txt') and response.status in (200,206) and (owner.control/'proxy-fail-version').exists():
                        self.send_response(503);self.send_header('Content-Length','0');self.end_headers();self.close_connection=True;return
                    if file_request:
                        with owner.lock:
                            owner.metrics['fileRequests']+=1;owner.metrics['activeFiles']+=1;registered=True
                            if response.status==206:
                                owner.metrics['rangeRequests']+=1
                                start=int(response.getheader('Content-Range').split()[1].split('-')[0])
                                owner.metrics['rangeStarts']=(owner.metrics['rangeStarts']+[start])[-32:]
                            owner.persist()
                    self.send_response(response.status)
                    for key,value in response.getheaders():
                        if key.lower() not in ('connection','transfer-encoding','server','date'):self.send_header(key,value)
                    self.close_connection=response.getheader('Content-Length') is None
                    if self.close_connection:self.send_header('Connection','close')
                    self.end_headers()
                    sent=0;started=time.monotonic()
                    throttled=path.endswith('/files/cancellation.bin')
                    if self.command!='HEAD':
                        while not owner.stop.is_set():
                            chunk=response.read(32768)
                            if not chunk:break
                            if throttled and not (owner.control/'proxy-unthrottle').exists():
                                delay=(sent+len(chunk))/owner.rate-(time.monotonic()-started)
                                if delay>0 and owner.stop.wait(delay):break
                            self.wfile.write(chunk);self.wfile.flush();sent+=len(chunk)
                            if file_request:
                                with owner.lock:
                                    owner.metrics['fileBytes']+=len(chunk)
                                    if throttled:owner.metrics['throttledBytes']+=len(chunk)
                                    owner.persist(force=False)
                except (OSError,http.client.HTTPException,ValueError):self.close_connection=True
                finally:
                    if registered:
                        with owner.lock:owner.metrics['activeFiles']=max(0,owner.metrics['activeFiles']-1);owner.persist()
                    if connection is not None:connection.close()
                    owner.slots.release()
            do_GET=forward
            do_HEAD=forward
        self.server=http.server.ThreadingHTTPServer(('127.0.0.1',public_port),Handler)
        self.server.daemon_threads=True
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True)
    def persist(self,force=True):
        now=time.monotonic()
        if not force and now-self.last_persist<.2:return
        self.last_persist=now
        temporary=self.control/'proxy-metrics.tmp'
        temporary.write_text(json.dumps(self.metrics),encoding='utf-8')
        for attempt in range(20):
            try:temporary.replace(self.control/'proxy-metrics.json');break
            except PermissionError:
                if attempt==19:raise
                time.sleep(.01)
    def start(self):
        with self.lock:
            previous=inside(self.root,'control/proxy-metrics.json')
            if previous.exists():
                if previous.stat().st_size>65536:raise ValueError('Oversized prior proxy summary')
                value=json.loads(previous.read_text());session=value.get('sessionId','legacy-'+uuid.uuid4().hex)
                if not all(c.isascii() and (c.isalnum() or c=='-') for c in session) or len(session)>64:raise ValueError('Invalid prior proxy session')
                history=inside(self.root,'control/proxy-sessions');history.mkdir(exist_ok=True)
                target=inside(history,session+'.json')
                if target.exists():raise ValueError('Prior proxy session was already archived')
                previous.replace(target)
            self.persist()
        self.thread.start()
    def fail_catalog_after_commit(self):
        with self.lock:
            arm=inside(self.root,'control/proxy-fail-catalog-after-commit')
            if not arm.exists():return False
            if arm.stat().st_size>65536:raise ValueError('Oversized Catalog fault binding')
            binding=json.loads(arm.read_text())
            records=list(inside(self.root,'client/state/operations').glob('*.json'))
            if len(records)>256:raise ValueError('Too many operation records')
            matching=[]
            for path in records:
                path=inside(self.root,str(path.relative_to(self.root)))
                if path.stem in binding['beforeIds']:continue
                if path.stat().st_size>65536:raise ValueError('Oversized operation')
                try:value=json.loads(path.read_text())
                except (OSError,ValueError):continue # Atomic replace can coincide with this test observation.
                expected={'projectId':'demo','environment':'prod','channel':'stable','platform':binding['platform'],'version':binding['version']}
                if value.get('schemaVersion')==1 and value.get('id')==path.stem and value.get('command')==binding['command'] and all((value.get('selection') or {}).get(k)==v for k,v in expected.items()):matching.append(value)
            if binding.get('operationId') is None:
                if len(matching)!=1:return False
                binding['operationId']=matching[0]['id']
                temporary=arm.with_suffix('.tmp');temporary.write_text(json.dumps(binding));temporary.replace(arm)
            for value in matching:
                if value['id']==binding['operationId'] and value.get('phase')=='Completed' and value.get('manifestSha256')==binding.get('manifestSha256'):
                    fired={'sessionId':self.metrics['sessionId'],'operationId':value['id'],'command':value['command'],'manifestSha256':value['manifestSha256'],'version':binding['version']}
                    temporary=self.control/'proxy-catalog-fired.tmp';temporary.write_text(json.dumps(fired));temporary.replace(self.control/'proxy-catalog-fired.json')
                    arm.unlink();self.metrics['catalogFaults']+=1;self.persist();return True
            return False
    def close(self):
        self.stop.set();self.server.shutdown();self.server.server_close();self.thread.join(timeout=5)
