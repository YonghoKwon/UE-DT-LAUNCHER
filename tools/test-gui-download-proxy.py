import hashlib
import http.client
import http.server
import json
from pathlib import Path
import tempfile
import threading
import unittest
from gui_download_proxy import DownloadProxy

class ProxyTests(unittest.TestCase):
    def test_restart_archives_the_previous_session_instead_of_overwriting_counts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)
            first=DownloadProxy(root,0,1);first.start()
            first.metrics['fileBytes']=12345
            with first.lock:first.persist()
            first_id=first.metrics['sessionId'];first.close()
            second=DownloadProxy(root,0,1);second.start()
            try:
                current=json.loads((root/'control/proxy-metrics.json').read_text())
                previous=json.loads((root/'control/proxy-sessions'/f'{first_id}.json').read_text())
                self.assertNotEqual(first_id,current['sessionId']);self.assertEqual(12345,previous['fileBytes'])
                self.assertEqual(0,current['fileBytes'])
            finally:second.close()

    def test_catalog_fault_is_exact_post_commit_one_shot_and_never_bypasses_denial(self):
        authorized=False
        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self,*_):pass
            def do_GET(self):
                self.send_response(200 if authorized else 401);self.send_header('Content-Length','2');self.end_headers();self.wfile.write(b'{}')
        server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Upstream)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            with tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);proxy=DownloadProxy(root,0,server.server_port);proxy.start()
                operations=root/'client/state/operations';operations.mkdir(parents=True)
                arm=root/'control/proxy-fail-catalog-after-commit'
                arm.write_text(json.dumps({'version':'2.0.0','platform':'windows-x64','beforeIds':[],'command':'repair','manifestSha256':'f'*64,'operationId':None}))
                record={'schemaVersion':1,'id':'a'*32,'phase':'Completed','command':'repair','manifestSha256':'f'*64,'selection':{'projectId':'demo','environment':'prod','channel':'stable','platform':'windows-x64','version':'2.0.0','releaseId':'demo/prod/stable/2.0.0/windows-x64'}}
                path=operations/(record['id']+'.json');path.write_text(json.dumps(record))
                def request():
                    connection=http.client.HTTPConnection('127.0.0.1',proxy.server.server_port,timeout=5)
                    connection.request('GET','/api/v1/catalog');response=connection.getresponse();response.read();connection.close();return response.status
                try:
                    self.assertEqual(401,request());self.assertTrue(arm.exists())
                    authorized=True;record['selection']['version']='1.0.0';path.write_text(json.dumps(record))
                    self.assertEqual(200,request());self.assertTrue(arm.exists())
                    record['selection']['version']='2.0.0';record['phase']='Applying';path.write_text(json.dumps(record))
                    self.assertEqual(200,request());self.assertTrue(arm.exists())
                    record['phase']='Completed';path.write_text(json.dumps(record))
                    self.assertEqual(503,request());self.assertFalse(arm.exists());self.assertEqual(200,request())
                    self.assertEqual(1,proxy.metrics['catalogFaults'])
                    fired=json.loads((root/'control/proxy-catalog-fired.json').read_text());self.assertEqual(record['id'],fired['operationId']);self.assertEqual('f'*64,fired['manifestSha256'])
                finally:proxy.close()
        finally:server.shutdown();server.server_close();thread.join(timeout=5)

    def test_query_signature_range_head_and_body_preserved_without_header_logs(self):
        observed=[];data=bytes(range(256))*1024
        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self,*_):pass
            def forward(self):
                observed.append((self.command,self.path,self.headers.get('Signature'),self.headers.get('Range')))
                partial=self.headers.get('Range')=='bytes=123-'
                body=data[123:] if partial else data
                self.send_response(206 if partial else 200)
                self.send_header('Content-Length',str(len(body)))
                if partial:self.send_header('Content-Range',f'bytes 123-{len(data)-1}/{len(data)}')
                self.end_headers()
                if self.command!='HEAD':self.wfile.write(body)
            do_GET=forward;do_HEAD=forward
        server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Upstream)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        with tempfile.TemporaryDirectory() as temporary:
            proxy=DownloadProxy(Path(temporary),0,server.server_port,1024*1024);proxy.start()
            try:
                connection=http.client.HTTPConnection('127.0.0.1',proxy.server.server_port,timeout=10)
                path='/releases/demo/files/cancellation.bin?x=%2F'
                headers={'Signature':'test-opaque-header','Range':'bytes=123-'}
                connection.request('GET',path,headers=headers);response=connection.getresponse();body=response.read()
                self.assertEqual(206,response.status);self.assertEqual(hashlib.sha256(data[123:]).digest(),hashlib.sha256(body).digest())
                connection.request('HEAD',path,headers=headers);self.assertEqual(b'',connection.getresponse().read());connection.close()
                self.assertEqual([('GET',path,'test-opaque-header','bytes=123-'),('HEAD',path,'test-opaque-header','bytes=123-')],observed)
                metrics=json.loads((Path(temporary)/'control/proxy-metrics.json').read_text())
                self.assertEqual(2,metrics['rangeRequests']);self.assertIn(123,metrics['rangeStarts'])
                self.assertEqual(len(body),metrics['fileBytes'])
                self.assertNotIn('test-opaque-header',json.dumps(metrics))
            finally:proxy.close()
        server.shutdown();server.server_close();thread.join(timeout=5)

if __name__=='__main__':unittest.main()
