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
