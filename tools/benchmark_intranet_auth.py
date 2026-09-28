"""Synthetic signed HTTP load; independent Python RFC 9421-profile producer.

No production credentials. One warm-up and three measured runs per concurrency.
Reports all attempts (including failed requests), not a company SLA or PERF-03 fix.
"""
import base64
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import secrets
import statistics
import time
from urllib.parse import urlsplit


def run_load(origin, root, register, platform, server_pid):
    from cryptography.hazmat.primitives.asymmetric import ec, utils
    from cryptography.hazmat.primitives import hashes, serialization
    parsed = urlsplit(origin)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1":
        raise ValueError("This harness only targets its isolated loopback HTTP fixture")
    key = ec.generate_private_key(ec.SECP256R1())
    key_id = "load-key"
    public_file = root / "load-public.json"
    public_file.write_text(json.dumps({"schemaVersion": 1, "keyId": key_id,
        "publicKeyPem": key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode()}))
    register(public_file)
    release_key = serialization.load_pem_public_key((root / "public.pem").read_bytes())

    def metrics():
        try:
            fields = Path(f"/proc/{server_pid}/stat").read_text().split(")", 1)[1].split()
            status = Path(f"/proc/{server_pid}/status").read_text().splitlines()
            peak = next(int(line.split()[1]) * 1024 for line in status if line.startswith("VmHWM:"))
            return {"cpu_seconds": (int(fields[11]) + int(fields[12])) / os.sysconf("SC_CLK_TCK"), "peak_rss_bytes": peak if peak > 0 else None}
        except (OSError, AttributeError, StopIteration):
            return {"cpu_seconds": None, "peak_rss_bytes": None}

    def verify(signature, message):
        raw = base64.b64decode(signature, validate=True)
        if len(raw) != 64:
            raise ValueError("Invalid raw signature size")
        der = utils.encode_dss_signature(int.from_bytes(raw[:32], "big"), int.from_bytes(raw[32:], "big"))
        release_key.verify(der, message, ec.ECDSA(hashes.SHA256()))

    class Client:
        def __init__(self):
            self.connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=20)
            self.connection.request("GET", "/api/v1/auth/challenge?keyId=" + key_id)
            response = self.connection.getresponse()
            document = json.loads(response.read())
            if response.status != 200:
                raise ValueError("Challenge startup rejected")
            self.challenge = document["challenge"]

        def work(self, count):
            latencies, errors = [], []
            for index in range(count):
                path = "/api/v1/catalog" if index % 2 == 0 else f"/releases/demo/prod/stable/2.0.0/{platform}/files/version.txt"
                nonce = base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")
                target = origin + path
                ranged = index % 2 != 0
                components = '("@method" "@target-uri" "x-ue-dt-challenge"' + (' "range"' if ranged else '') + ')'
                parameters = components + f';keyid="{key_id}";nonce="{nonce}";alg="ecdsa-p256-sha256";tag="ue-dt-request-v1"'
                body = f'"@method": GET\n"@target-uri": {target}\n"x-ue-dt-challenge": {self.challenge}\n'
                if ranged:
                    body += '"range": bytes=0-2\n'
                body += '"@signature-params": ' + parameters
                start = time.perf_counter()
                try:
                    r, s = utils.decode_dss_signature(key.sign(body.encode(), ec.ECDSA(hashes.SHA256())))
                    signed = base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode()
                    headers = {"X-UE-DT-Challenge": self.challenge, "Signature-Input": "sig1=" + parameters, "Signature": "sig1=:" + signed + ":"}
                    if ranged:
                        headers["Range"] = "bytes=0-2"
                    self.connection.request("GET", path, headers=headers)
                    response = self.connection.getresponse()
                    data = response.read()
                    if response.status != (206 if ranged else 200):
                        raise ValueError("HTTP " + str(response.status))
                    if ranged:
                        if data != b"2.0":
                            raise ValueError("Range content mismatch")
                    else:
                        envelope = json.loads(data)
                        payload = base64.b64decode(envelope["payload"], validate=True)
                        verify(json.loads(envelope["signatureDocument"])["signature"], payload)
                        binding = envelope["requestBinding"]
                        digest = hashlib.sha256(payload).hexdigest()
                        if (binding["keyId"], binding["nonce"], binding["method"], binding["targetUri"], binding["payloadSha256"]) != (key_id, nonce, "GET", target, digest):
                            raise ValueError("Catalog binding mismatch")
                        message = json.dumps(["ue-dt-catalog-request-binding-v1", key_id, nonce, "GET", target, digest], separators=(",", ":")).encode()
                        verify(json.loads(binding["signatureDocument"])["signature"], message)
                except Exception as error:
                    errors.append(type(error).__name__ + (": " + str(error) if isinstance(error, ValueError) else ""))
                latencies.append((time.perf_counter() - start) * 1000)
            return latencies, errors

    report = {"transport": "HTTP request-signature-v1", "workload": "alternating fresh catalog and 3-byte Range", "warmup_runs": 1,
              "measured_runs": 3, "requests_per_client_per_run": 20, "includes_client_signing_and_response_verification": True,
              "cache_condition": "same published fixture; OS cache not flushed; challenge/connection reused within each group",
              "metrics_start": metrics(), "results": []}
    for concurrency in (1, 10, 30):
        time.sleep(1.1)  # Separate startup bursts from the per-IP challenge rate window.
        clients = [Client() for _ in range(concurrency)]
        measured = []
        with ThreadPoolExecutor(max_workers=concurrency) as pool:
            for repeat in range(4):
                start = time.perf_counter()
                results = list(pool.map(lambda client: client.work(20), clients))
                duration = time.perf_counter() - start
                values = sorted(value for latency, _ in results for value in latency)
                errors = [error for _, failures in results for error in failures]
                row = {"seconds": duration, "requests": len(values), "failures": len(errors), "errors": sorted(set(errors)),
                       "p50_ms": statistics.median(values), "p95_ms": values[math.ceil(len(values) * 0.95) - 1]}
                if repeat:
                    measured.append(row)
                else:
                    warmup = row
        for client in clients:
            client.connection.close()
        report["results"].append({"clients": concurrency, "warmup": warmup, "runs": measured,
                                  "median_p50_ms": statistics.median(row["p50_ms"] for row in measured),
                                  "median_p95_ms": statistics.median(row["p95_ms"] for row in measured),
                                  "failed_requests": sum(row["failures"] for row in measured)})
    report["metrics_end"] = metrics()
    (root / "load-summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if any(row["failed_requests"] or row["warmup"]["failures"] for row in report["results"]):
        raise RuntimeError("Signed load validation had failures; retain the report and do not claim success")
    return report
