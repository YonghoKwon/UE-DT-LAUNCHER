#!/usr/bin/env python3
"""Isolated, deterministic published-process benchmark. Requires Python 3.11+ and psutil.

Loopback HTTP uses request-signature-v1 and signed metadata, never HTTP/Bearer.
HTTPS/Bearer is measured separately; transports are not interchangeable baselines.
Never pass a company server or installation directory: a fresh output root is required.
"""
import argparse
import concurrent.futures
import hashlib
import http.client
import http.server
import json
import os
import queue
from pathlib import Path
import random
import shutil
import socket
import statistics
import subprocess
import threading
import time
import urllib.request
import zipfile

import psutil
import ssl
from promotion_fixture_support import promote
from benchmark_intranet_auth import run_load
from fixture_contract import claim, seal
from evidence_contract import provenance, atomic
from stream_hash import sha256_stream


PROFILES = {"small": [(1000, 4096)], "large": [(10, 8 * 1024 * 1024)],
            "mixed": [(900, 4096), (10, 4 * 1024 * 1024)]}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run(command, log, env=None):
    started = time.perf_counter()
    arguments = [str(x) for x in command]
    secret = 'token-issue' in arguments or ('credential' in arguments and ('keygen' in arguments or 'set' in arguments))
    if secret:
        result = subprocess.run(arguments, capture_output=True, env=env, timeout=120,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        log.write_text('sensitive provisioning output omitted\n', encoding='utf-8')
        if result.returncode:
            raise RuntimeError('Sensitive provisioning failed')
        return {'wallMs': (time.perf_counter()-started)*1000, 'cpuSecondsSampled': None,
                'peakRssBytesSampled': None, '_secretOutput': result.stdout.decode('utf-8-sig').strip()}
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen([str(x) for x in command], stdout=output,
                                   stderr=subprocess.STDOUT, env=env,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        watched = psutil.Process(process.pid)
        peak = cpu = None
        while process.poll() is None:
            try:
                memory = watched.memory_info()
                peak = max(peak or 0, getattr(memory, "peak_wset", memory.rss))
                t = watched.cpu_times()
                cpu = max(cpu or 0, t.user + t.system)
            except psutil.NoSuchProcess:
                pass
            time.sleep(.02)
    if process.returncode:
        raise RuntimeError(f"Process failed ({process.returncode}); inspect {log}")
    return {"wallMs": (time.perf_counter() - started) * 1000,
            "cpuSecondsSampled": cpu, "peakRssBytesSampled": peak,"missingMetricReason":"no-owned-process-samples" if peak is None else None}


class Proxy(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass  # Never record Authorization or request headers.

    def do_GET(self):
        try:
            connection = self.server.connections.get_nowait()
        except queue.Empty:
            connection = http.client.HTTPConnection("127.0.0.1", self.server.upstream, timeout=120)
        reusable = False
        try:
            if "/files/" in self.path:
                time.sleep(self.server.delay)
            connection.request("GET", self.path,
                               headers={k: v for k, v in self.headers.items()
                                        if k.lower() not in ("host", "connection")})
            response = connection.getresponse()
            self.send_response(response.status)
            for key, value in response.getheaders():
                if key.lower() not in ("connection", "transfer-encoding", "server", "date"):
                    self.send_header(key, value)
            # Preserve connection reuse: opening a socket for every synthetic file can
            # exhaust Windows ephemeral ports and measure the harness, not the launcher.
            self.close_connection = response.getheader("Content-Length") is None
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            while chunk := response.read(65536):
                self.wfile.write(chunk)
                if "/files/" in self.path:
                    with self.server.counter_lock:
                        self.server.content_bytes += len(chunk)
            reusable = not response.will_close
        except (OSError, http.client.HTTPException):
            self.close_connection = True
        finally:
            if reusable:
                try:
                    self.server.connections.put_nowait(connection)
                except queue.Full:
                    connection.close()
            else:
                connection.close()


class ProxyServer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 256


def fixture(upload, profile, version, platform):
    upload.mkdir(parents=True)
    total = unchanged = 0
    with zipfile.ZipFile(upload / "Package.zip", "w", compression=zipfile.ZIP_STORED) as archive:
        for group, (count, size) in enumerate(PROFILES[profile]):
            for index in range(count):
                changed = version == "2.0.0" and index % 10 == 0
                rng = random.Random(group * 100000 + index + (999999 if changed else 0))
                data = rng.randbytes(size)
                archive.writestr(f"g{group}/f{index:05}.bin", data)
                total += size
                if index % 10:
                    unchanged += size
    package = upload / "Package.zip"
    with package.open("rb") as stream:
        digest = sha256_stream(stream)
    write_json(upload / "release.json", {
        "schemaVersion": 1, "packageFile": package.name, "packageSize": package.stat().st_size,
        "packageSha256": digest, "projectId": "bench-" + profile, "displayName": "Benchmark " + profile,
        "version": version, "environment": "prod", "channel": "stable", "platform": platform,
        "payloadRoot": ".", "entryPoint": "g0/f00000.bin", "executablePaths": []})
    assert unchanged * 10 == total * 9
    return total


def phase_metrics(log_dir):
    """Old log boundaries are wall-time estimates, not cumulative worker timings."""
    entries = []
    for path in log_dir.glob("*.jsonl"):
        # Legacy JSONL contains pretty-printed objects; parse consecutive JSON values.
        text = path.read_text(encoding="utf-8-sig")
        decoder = json.JSONDecoder()
        position = 0
        while position < len(text):
            while position < len(text) and text[position].isspace():
                position += 1
            if position == len(text):
                break
            item, position = decoder.raw_decode(text, position)
            entries.append(item)
    for entry in reversed(entries):
        if entry.get("stage") == "Performance":
            try:
                return {"kind": "engine", **json.loads(entry["message"])}
            except (ValueError, TypeError):
                pass
    from datetime import datetime
    times = {}
    for entry in entries:
        stage = entry.get("stage")
        if stage and stage not in times:
            times[stage] = datetime.fromisoformat(entry["timestampUtc"]).timestamp()
    spans = {}
    for name, begin, end in [("planAndHashMs", "Plan", "Download"),
                             ("downloadAndVerifyMs", "Download", "Apply"),
                             ("applyMs", "Apply", "Complete")]:
        spans[name] = max(0, (times[end] - times[begin]) * 1000) if begin in times and end in times else None
    return {"kind": "legacy-log-boundaries", **spans, "copyMs": None}


def percentile(values, fraction):
    values = sorted(values)
    return values[min(len(values) - 1, int((len(values) - 1) * fraction))]


def api_load(base, token, clients, requests, path):
    def worker(_):
        samples = []
        for _ in range(requests):
            started = time.perf_counter()
            status = 0
            try:
                request = urllib.request.Request(base + path, headers={"Authorization": "Bearer " + token})
                with urllib.request.urlopen(request, timeout=120) as response:
                    response.read()
                    status = response.status
            except Exception:
                pass
            samples.append(((time.perf_counter() - started) * 1000, status))
        return samples
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=clients) as pool:
        samples = [item for batch in pool.map(worker, range(clients)) for item in batch]
    latencies = [item[0] for item in samples]
    return {"clients": clients, "requests": len(samples), "p50Ms": percentile(latencies, .5),
            "p95Ms": percentile(latencies, .95), "errors": sum(status != 200 for _, status in samples),
            "wallMs": (time.perf_counter() - started) * 1000}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launcher", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="Must not exist")
    parser.add_argument("--profiles", default="small,large,mixed")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--latency-ms", type=float, default=5)
    parser.add_argument("--download-concurrency", type=int, default=2)
    parser.add_argument("--hash-concurrency", type=int, default=2)
    parser.add_argument("--no-reuse", action="store_true")
    parser.add_argument("--corrupt-source-record", action="store_true",
                        help="Test fallback by replacing this fresh fixture's old install-state with JSON null")
    parser.add_argument("--workers", type=int, default=1)
    parser.add_argument("--authentication-mode",choices=['request-signature-v1','bearer'],default='request-signature-v1',help='Bearer always uses private loopback TLS')
    args = parser.parse_args()
    if args.runs < 1:
        parser.error("--runs must be positive")
    profiles = args.profiles.split(",")
    if any(name not in PROFILES for name in profiles):
        parser.error("Unknown profile")
    args.launcher = args.launcher.resolve(strict=True)
    args.server = args.server.resolve(strict=True)
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    claim(root)
    backend = port()
    proxy = ProxyServer(("127.0.0.1", 0), Proxy)
    proxy.upstream, proxy.delay = backend, args.latency_ms / 1000
    proxy.counter_lock, proxy.content_bytes = threading.Lock(), 0
    proxy.connections = queue.Queue(maxsize=64)
    tls=args.authentication_mode=='bearer'
    base = f"{'https' if tls else 'http'}://127.0.0.1:{proxy.server_port}"
    if tls:
        from benchmark_tls_support import context
        proxy.socket=context(root).wrap_socket(proxy.socket,server_side=True)
    env = {**os.environ, "UE_DT_AGENT_DATA_ROOT": str(root / "credentials")}
    run([args.launcher, "generate-signing-key", "--private-key", root / "key.pem",
         "--public-key", root / "public.pem"], root / "keys.log", env)
    write_json(root / "server.json", {"root": str(root / "server"), "publicUrl": base,
               "listenUrl": f"http://127.0.0.1:{backend}", "signingKeyPath": str(root / "key.pem"),
               "policyPath": str(root / "policy.json"), "intakeWorkers": args.workers,
               "authenticationMode": args.authentication_mode})
    write_json(root / "policy.json", {"clients": [{"id": "bench", "addresses": ["127.0.0.1"],
               "grants": [{"projectId": "bench-" + p, "environment": "prod", "channel": "stable", "versions": []}
                          for p in profiles]}]})
    seal(root, {'launcher': args.launcher, 'server': args.server}, root / 'server.json')
    server_cli = [args.server, "--config", root / "server.json"]
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    report = {"schemaVersion": 1, "complete": False, **provenance({'launcher': args.launcher, 'server': args.server}),
              "platform": platform, "runs": args.runs, "warmups": 1,
              "transport": "loopback HTTPS/Bearer + signed metadata" if tls else "loopback HTTP request-signature-v1 + signed metadata",
              "fileRequestDelayMs": args.latency_ms, "cacheCondition": "fresh app/state per iteration; OS cache not flushed",
              "settings": {"downloads": args.download_concurrency, "hashes": args.hash_concurrency,
                           "reuse": not args.no_reuse, "intakeWorkers": args.workers,
                           "corruptSourceRecord": args.corrupt_source_record},
              "client": [], "api": [], "intake": [], "sqliteBusyWaitMs": None}
    atomic(root / 'results.json', report)
    for profile in profiles:
        for version in ("1.0.0", "2.0.0"):
            upload = root / "server/incoming" / (profile + "-" + version)
            size = fixture(upload, profile, version, platform)
            measurement = run([*server_cli, "ingest", upload], root / "ingest.json", env)
            job = json.loads((root / "ingest.json").read_text(encoding="utf-8-sig"))
            if job["state"] != "pending":
                raise RuntimeError("Fixture intake failed: " + str(job))
            approval = run([*server_cli, "approve", job["id"]], root / "approve.json", env)
            report["intake"].append({"profile": profile, "version": version, "payloadBytes": size,
                                     "ingest": measurement, "approve": approval})
            atomic(root / 'results.json', report)
    if not tls:
        run([args.launcher, "credential", "keygen", "--name", "benchmark", "--key-id", "bench-cli",
             "--public-out", root / "device-public.json"], root / "credential.log", env)
        run([*server_cli, "client-key", "add", "--client", "bench", "--public-key", root / "device-public.json"], root / "register.log", env)
    if tls:
        token=run([*server_cli,'token-issue','bench'],root/'token-private.log',env).pop('_secretOutput')
        run([args.launcher,'credential','set','--name','benchmark'],root/'bearer.log',{**env,'UE_DT_CREDENTIAL_TOKEN':token})
    for profile in profiles:
        # Approval never changes recommendations; baseline explicitly records promotion.
        def execute(*arguments):
            run([args.server, *arguments], root / "promotion-command.json", env)
            return (root / "promotion-command.json").read_text(encoding="utf-8-sig")
        promote(execute, root / "server.json", "bench-" + profile, "2.0.0", platform)
    with (root / "server.log").open("w", encoding="utf-8") as output:
        server = subprocess.Popen([str(x) for x in [*server_cli, "serve"]], stdout=output,
                                  stderr=subprocess.STDOUT, env=env,
                                  creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        thread = threading.Thread(target=proxy.serve_forever, daemon=True)
        thread.start()
        try:
            for _ in range(100):
                try:
                    with socket.create_connection(("127.0.0.1", backend), timeout=.1):
                        break
                except OSError:
                    if server.poll() is not None:
                        raise RuntimeError("Server exited; inspect server.log")
                    time.sleep(.1)
            for profile in profiles:
                for iteration in range(args.runs + 1):
                    client = root / f"client-{profile}-{iteration}"
                    for scenario, version in [("install", "1.0.0"), ("unchanged", "1.0.0"),
                                              ("next-version", "2.0.0"), ("repair", "2.0.0")]:
                        log_dir = client / ("logs-" + scenario)
                        config = {"schemaVersion": 3, "distributionServerUrl": base,
                                  "projectId": "bench-" + profile, "clientProfile": "developer",
                                  "environment": "prod", "channel": "stable", "versionPolicy": "exact",
                                  "requestedVersion": version, "targetPlatform": platform,
                                  "installDir": str(client / "apps"), "stateRootDir": str(client / "state"),
                                  "logDir": str(log_dir), "requireSignedManifests": True,
                                  "performance": {"downloadConcurrency": args.download_concurrency,
                                                  "hashConcurrency": args.hash_concurrency,
                                                  "reusePreviousInstallations": not args.no_reuse},
                                  "security": {"requireHttps": tls, "credentialName": "benchmark", "authenticationMode": args.authentication_mode,
                                               "customCaCertificatePath":str(root/'tls.pem') if tls else None,
                                               "allowedDownloadHosts": ["127.0.0.1"],
                                               "trustedSigningKeys": [{"keyId": "release-1", "publicKeyPath": str(root / "public.pem")}]}}
                        write_json(client / "config.json", config)
                        if scenario == "next-version" and args.corrupt_source_record:
                            old_state = client / "state" / ("bench-" + profile) / "prod/stable/1.0.0" / platform / "install-state.json"
                            old_state.write_text("null", encoding="utf-8")
                        if scenario == "repair":
                            target = client / "apps" / ("bench-" + profile) / "prod/stable/2.0.0" / platform / "g0/f00000.bin"
                            target.write_bytes(b"damaged fixture")
                        before = proxy.content_bytes
                        command = [args.launcher, "run", "--config", client / "config.json", "--no-launch"]
                        if scenario == "repair":
                            command.append("--repair")
                        metrics = run(command, client / (scenario + ".log"), env)
                        metrics.update(profile=profile, iteration=iteration, warmup=iteration == 0,
                                       scenario=scenario, contentNetworkBytes=proxy.content_bytes - before,
                                       stages=phase_metrics(log_dir))
                        report["client"].append(metrics)
                        atomic(root / 'results.json', report)
                        print(f"{profile} {iteration} {scenario}: {metrics['wallMs']:.0f}ms / {metrics['contentNetworkBytes']} bytes", flush=True)
                    # Only delete generated app/state inside this fresh, owned output root.
                    for directory in (client / "apps", client / "state"):
                        resolved = directory.resolve()
                        if root not in resolved.parents or directory.is_symlink():
                            raise RuntimeError("Refusing cleanup outside isolated benchmark root")
                        shutil.rmtree(resolved)
                    write_json(root / "results.json", report)
            first = profiles[0]
            target = f"/releases/bench-{first}/prod/stable/2.0.0/{platform}/files/g0/f00000.bin"
            # Deterministic changed g0/f0 bytes, identical to fixture().
            expected = random.Random(999999).randbytes(PROFILES[first][0][1])[:3]
            if tls:
                from benchmark_tls_support import run_tls_load
                report['api']=run_tls_load(base,root,token,target,expected,server.pid)['results']
            else: report["api"] = run_load(base, root,
                lambda path: run([*server_cli, "client-key", "add", "--client", "bench", "--public-key", path], root / "load-register.log", env),
                platform, server.pid, target, expected)["results"]
        finally:
            atomic(root / 'results.json', report)
            proxy.shutdown()
            proxy.server_close()
            while not proxy.connections.empty():
                proxy.connections.get_nowait().close()
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
    server_log = (root / "server.log").read_text(encoding="utf-8", errors="replace")
    report["sqliteBusyErrorLines"] = sum("database is locked" in line.lower() or "SQLite Error 5" in line
                                        for line in server_log.splitlines())
    report['complete'] = True
    atomic(root / "results.json", report)
    lines = ["# Synthetic launcher benchmark", "", report["transport"], "", report["cacheCondition"],
             "", "| Profile | Scenario | Median ms | Median content bytes |", "|---|---|---:|---:|"]
    for profile in profiles:
        for scenario in ("install", "unchanged", "next-version", "repair"):
            rows = [r for r in report["client"] if not r["warmup"] and r["profile"] == profile and r["scenario"] == scenario]
            lines.append(f"| {profile} | {scenario} | {statistics.median(r['wallMs'] for r in rows):.1f} | {statistics.median(r['contentNetworkBytes'] for r in rows):.0f} |")
    lines += ["", "| Clients | Median p95 ms | Errors |", "|---|---:|---:|"]
    for clients in (1, 10, 30):
        row = next(r for r in report["api"] if r["clients"] == clients)
        lines.append(f"| {clients} | {row['median_p95_ms']:.1f} | {row['failed_requests']} |")
    lines += ["", "CPU/RSS are 20ms process samples, not exact counters. Legacy phase times are log boundaries.",
              "SQLite busy duration is unavailable on the baseline; null is not zero. Intake CLI includes process startup.",
              "No real UE, RHEL, WAN, production SLA or cold-OS-cache claim is made."]
    (root / "results.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("Report: " + str(root / "results.json"), flush=True)


if __name__ == "__main__":
    main()
