"""Private TLS CA and independent HTTPS/Bearer load; no host trust-store edits."""
from datetime import datetime, timedelta, timezone
import base64
from concurrent.futures import ThreadPoolExecutor
import http.client
import ipaddress
import json
import math
import ssl
import statistics
import time
from urllib.parse import urlsplit
from benchmark_resources import ProcessSampler


def context(root):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import NameOID
    key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
    subject=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'Isolated benchmark TLS')])
    now=datetime.now(timezone.utc)
    cert=(x509.CertificateBuilder().subject_name(subject).issuer_name(subject).public_key(key.public_key()).serial_number(x509.random_serial_number())
          .not_valid_before(now-timedelta(minutes=5)).not_valid_after(now+timedelta(days=1))
          .add_extension(x509.SubjectAlternativeName([x509.IPAddress(ipaddress.ip_address('127.0.0.1'))]),critical=False)
          .add_extension(x509.BasicConstraints(ca=True,path_length=0),critical=True).sign(key,hashes.SHA256()))
    (root/'tls.pem').write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    (root/'tls-key.pem').write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
    server=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);server.load_cert_chain(root/'tls.pem',root/'tls-key.pem');return server


def run_tls_load(origin,root,token,file_path,expected,pid):
    parsed=urlsplit(origin)
    if parsed.scheme!='https' or parsed.hostname!='127.0.0.1':raise ValueError('Only owned loopback TLS fixtures')
    from cryptography.hazmat.primitives import hashes,serialization
    from cryptography.hazmat.primitives.asymmetric import ec,utils
    key=serialization.load_pem_public_key((root/'public.pem').read_bytes())
    trust=ssl.create_default_context(cafile=str(root/'tls.pem'))
    def work(connection):
        latencies=[];errors=[]
        for index in range(20):
            started=time.perf_counter();ranged=index%2!=0
            try:
                headers={'Authorization':'Bearer '+token}
                if ranged:headers['Range']='bytes=0-2'
                connection.request('GET',file_path if ranged else '/api/v1/catalog?selectionPolicy=explicit-promotion-v1',headers=headers)
                response=connection.getresponse();body=response.read()
                if response.status!=(206 if ranged else 200):raise ValueError('HTTP-'+str(response.status))
                if ranged:
                    if body!=expected:raise ValueError('Range mismatch')
                else:
                    envelope=json.loads(body);payload=base64.b64decode(envelope['payload']);signature=base64.b64decode(json.loads(envelope['signatureDocument'])['signature'])
                    der=utils.encode_dss_signature(int.from_bytes(signature[:32],'big'),int.from_bytes(signature[32:],'big'));key.verify(der,payload,ec.ECDSA(hashes.SHA256()))
            except Exception as error:errors.append(type(error).__name__)
            latencies.append((time.perf_counter()-started)*1000)
        return latencies,errors
    report={'transport':'HTTPS/Bearer','warmup_runs':1,'measured_runs':3,'requests_per_client_per_run':20,'tls_trust':'private CA; no host store modification','results':[]}
    with ProcessSampler(pid) as sampler:
        for count in (1,10,30):
            clients=[http.client.HTTPSConnection(parsed.hostname,parsed.port,context=trust,timeout=20) for _ in range(count)]
            runs=[]
            with ThreadPoolExecutor(max_workers=count) as pool:
                for repeat in range(4):
                    batches=list(pool.map(work,clients));values=sorted(v for times,_ in batches for v in times);failures=[e for _,errors in batches for e in errors]
                    row={'requests':len(values),'failures':len(failures),'errors':sorted(set(failures)),'p50_ms':statistics.median(values),'p95_ms':values[math.ceil(len(values)*.95)-1]}
                    if repeat:runs.append(row)
                    else:warmup=row
            for client in clients:client.close()
            report['results'].append({'clients':count,'warmup':warmup,'runs':runs,'median_p50_ms':statistics.median(r['p50_ms'] for r in runs),'median_p95_ms':statistics.median(r['p95_ms'] for r in runs),'failed_requests':sum(r['failures'] for r in runs)})
            (root/'load-summary.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    report['resources']=sampler.result();(root/'load-summary.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    if any(r['failed_requests'] or r['warmup']['failures'] for r in report['results']):raise RuntimeError('TLS load failed; retain report')
    return report
