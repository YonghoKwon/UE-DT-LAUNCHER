#!/usr/bin/env python3
"""Supplemental mixed catalog/Range benchmark of a published distribution server.

Only completed synthetic fixtures from benchmark-launcher-performance.py are accepted.
One warmup and three measured runs use 1, 10 and 30 persistent HTTP connections,
with 20 alternating catalog/Range requests per connection. This is loopback HTTP,
not an HTTPS/nginx, WAN, company RHEL, or production-SLA measurement.

The input config, policy, packages and manifests are never edited. The published
server necessarily writes its fixture SQLite DB (a new bench token, sequences and
normal startup migrations). Run only after the original benchmark process stops.
"""
import argparse
import base64
from collections import Counter
import concurrent.futures
from contextlib import closing
import http.client
import json
import os
from pathlib import Path
import re
import signal
import socket
import sqlite3
import statistics
import subprocess
import threading
import time
from urllib.parse import quote, urlsplit
from benchmark_intranet_auth import run_load
from metric_contract import sanitize
from fixture_contract import verify, claim
from evidence_contract import provenance, atomic


CLIENT_COUNTS = (1, 10, 30)
REQUESTS_PER_CLIENT = 20
MEASURED_RUNS = 3
MAX_DOCUMENT_BYTES = 8 * 1024 * 1024
MAX_RESPONSE_BYTES = 2 * 1024 * 1024
SEGMENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z")


def read_json(path, limit=MAX_DOCUMENT_BYTES):
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("Synthetic fixture document exceeds the benchmark size cap")
    return json.loads(data.decode("utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def contained(path, root):
    resolved = path.resolve(strict=True)
    if root not in resolved.parents:
        raise ValueError("Fixture path escapes its synthetic benchmark root")
    return resolved


def loopback_url(value):
    uri = urlsplit(value)
    return (uri.scheme == "http" and uri.hostname == "127.0.0.1" and uri.port is not None
            and not uri.username and not uri.password and not uri.query and not uri.fragment
            and uri.path in ("", "/"))


def validate_fixture(config_path, server):
    config_path = config_path.resolve(strict=True)
    if config_path.name != "server.json":
        raise ValueError("Expected the synthetic benchmark server.json")
    fixture = config_path.parent
    verify(fixture, config_path, server)
    report = read_json(fixture / "results.json")
    if report.get("schemaVersion") != 1 or not str(report.get("transport", "")).startswith("loopback HTTP"):
        raise ValueError("Missing synthetic benchmark results.json marker")
    if not report.get("api") or not report.get("intake"):
        raise ValueError("The original synthetic benchmark must complete before this supplemental run")
    config = read_json(config_path, 64 * 1024)
    root = Path(config["root"]).resolve(strict=True)
    if root != fixture / "server" or not root.is_dir():
        raise ValueError("Only the original benchmark's sibling server/ fixture is accepted")
    if not loopback_url(config["listenUrl"]) or not loopback_url(config["publicUrl"]):
        raise ValueError("Only the original loopback HTTP fixture is accepted")
    policy_path = Path(config["policyPath"]).resolve(strict=True)
    key_path = Path(config["signingKeyPath"]).resolve(strict=True)
    if policy_path != fixture / "policy.json" or key_path != fixture / "key.pem":
        raise ValueError("Policy and signing key must belong to the synthetic benchmark fixture")
    policy = read_json(policy_path)
    clients = policy.get("clients")
    if not isinstance(clients, list) or len(clients) != 1:
        raise ValueError("Synthetic fixture must contain exactly the bench client")
    client = clients[0]
    if client.get("id") != "bench" or client.get("addresses") != ["127.0.0.1"]:
        raise ValueError("Policy is not the original loopback bench fixture")
    grants = client.get("grants")
    if not isinstance(grants, list) or not grants or any(
            not str(g.get("projectId", "")).startswith("bench-") or g.get("environment") != "prod"
            or g.get("channel") != "stable" or g.get("versions") != [] for g in grants):
        raise ValueError("Policy is not the original synthetic benchmark grant set")
    database = contained(root / "distribution.db", root)
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as db:
        row = db.execute("SELECT id,directory,metadata FROM releases ORDER BY rowid LIMIT 1").fetchone()
    if row is None:
        raise ValueError("Synthetic fixture has no published release")
    release_id, release_directory, metadata_json = row
    parts = release_id.split("/")
    if (len(parts) != 5 or not all(SEGMENT.fullmatch(p) for p in parts)
            or not parts[0].startswith("bench-") or parts[1:3] != ["prod", "stable"]):
        raise ValueError("Published release is outside the synthetic benchmark namespace")
    if len(metadata_json.encode("utf-8")) > 64 * 1024:
        raise ValueError("Synthetic release metadata exceeds the benchmark cap")
    metadata = json.loads(metadata_json)
    if metadata.get("projectId") not in {g["projectId"] for g in grants}:
        raise ValueError("Published fixture release is not granted to bench")
    releases_root = contained(root / "releases", root)
    directory = contained(Path(release_directory), releases_root)
    manifest_path = contained(directory / "manifest.json", directory)
    manifest = read_json(manifest_path)
    files = manifest.get("files")
    if not isinstance(files, list) or not files:
        raise ValueError("Published synthetic manifest has no files")
    file = files[0]
    relative = file["path"].replace("\\", "/")
    if (not relative or any(p in ("", ".", "..") for p in relative.split("/"))
            or any(ord(c) < 32 or c in ':*?"<>|' for c in relative)):
        raise ValueError("Invalid synthetic manifest file path")
    size = file.get("size")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise ValueError("First synthetic file must have a positive size for Range testing")
    payload = contained(directory / "files" / relative, directory / "files")
    if payload.stat().st_size != size:
        raise ValueError("Synthetic file size no longer matches its published manifest")
    range_size = min(1024, size)
    with payload.open("rb") as stream:
        expected = stream.read(range_size)
    return config, {
        "releaseId": release_id, "filePath": "/releases/" + quote(release_id, safe="/") + "/files/" + quote(relative, safe="/"),
        "fileSize": size, "rangeSize": range_size, "rangeHeader": f"bytes=0-{range_size - 1}",
        "contentRange": f"bytes 0-{range_size - 1}/{size}", "expectedBytes": expected,
    }


def free_port():
    with socket.socket() as connection:
        connection.bind(("127.0.0.1", 0))
        return connection.getsockname()[1]


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int((len(ordered) - 1) * fraction))]


def summarize(samples):
    durations = [sample["elapsedMs"] for sample in samples]
    successful = [sample["elapsedMs"] for sample in samples if sample["error"] is None]
    return {
        "requests": len(samples), "errors": sum(sample["error"] is not None for sample in samples),
        "statusCounts": dict(sorted(Counter(str(sample["status"]) for sample in samples).items())),
        "errorKinds": dict(sorted(Counter(sample["error"] for sample in samples if sample["error"]).items())),
        "receivedBytes": sum(sample["bytes"] for sample in samples),
        "p50Ms": percentile(durations, .5), "p95Ms": percentile(durations, .95),
        "successP50Ms": percentile(successful, .5), "successP95Ms": percentile(successful, .95),
    }


def load(port, token, clients, selection):
    barrier = threading.Barrier(clients)

    def worker(_):
        samples = []
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=60)
        try:
            barrier.wait(timeout=30)
            for index in range(REQUESTS_PER_CLIENT):
                kind = "catalog" if index % 2 == 0 else "range"
                path = "/api/v1/catalog" if kind == "catalog" else selection["filePath"]
                headers = {"Authorization": "Bearer " + token}
                if kind == "range":
                    headers["Range"] = selection["rangeHeader"]
                status = received = 0
                error = sequence = None
                body = b""
                response = None
                started = time.perf_counter()
                try:
                    connection.request("GET", path, headers=headers)
                    response = connection.getresponse()
                    status = response.status
                    body = response.read(MAX_RESPONSE_BYTES + 1)
                    received = len(body)
                    if received > MAX_RESPONSE_BYTES:
                        error = "response-size-cap"
                        connection.close()
                except (OSError, http.client.HTTPException):
                    error = "transport"
                    connection.close()
                elapsed = (time.perf_counter() - started) * 1000
                # Validate outside the request timer; never copy raw payloads/headers into result files.
                if error is None and status != (200 if kind == "catalog" else 206):
                    error = "unexpected-status"
                if error is None and kind == "range":
                    if body != selection["expectedBytes"] or response.getheader("Content-Range") != selection["contentRange"]:
                        error = "range-content"
                if error is None and kind == "catalog":
                    try:
                        envelope = json.loads(body)
                        if not isinstance(envelope, dict) or not isinstance(envelope.get("signatureDocument"), str):
                            raise ValueError("Missing signature")
                        catalog = json.loads(base64.b64decode(envelope["payload"], validate=True))
                        if not isinstance(catalog, dict):
                            raise ValueError("Invalid catalog object")
                        sequence = catalog["sequence"]
                        if (not isinstance(sequence, int) or isinstance(sequence, bool) or sequence <= 0
                                or "no-store" not in (response.getheader("Cache-Control") or "").lower()):
                            raise ValueError("Invalid catalog metadata")
                    except (ValueError, KeyError, TypeError):
                        error = "catalog-shape"
                        sequence = None
                samples.append({"kind": kind, "elapsedMs": elapsed, "status": status,
                                "error": error, "bytes": received, "sequence": sequence})
        finally:
            connection.close()
        return samples

    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=clients) as pool:
        samples = [sample for batch in pool.map(worker, range(clients)) for sample in batch]
    sequences = [sample["sequence"] for sample in samples if sample["sequence"] is not None]
    seen = set()
    for sample in samples:
        sequence = sample["sequence"]
        if sequence is not None:
            if sequence in seen:
                sample["error"] = "catalog-replayed-sequence"
            seen.add(sequence)
    return {
        "clients": clients, "requestsPerClient": REQUESTS_PER_CLIENT, **summarize(samples),
        "wallMs": (time.perf_counter() - started) * 1000,
        "workloads": {kind: summarize([sample for sample in samples if sample["kind"] == kind]) for kind in ("catalog", "range")},
        "uniqueCatalogSequences": len(set(sequences)), "duplicateCatalogSequences": len(sequences) - len(set(sequences)),
    }


def await_server(server, port):
    for _ in range(300):
        if server.poll() is not None:
            raise RuntimeError("Published server exited before readiness; inspect server.log")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=.1):
                return
        except OSError:
            time.sleep(.1)
    raise RuntimeError("Published server did not become ready within 30 seconds")


def stop_server(server):
    if server.poll() is not None:
        return {"method": "already-exited", "exitCode": server.returncode, "gracefulRequested": False}
    method = "terminate" if os.name == "nt" else "SIGINT"
    try:
        if os.name == "nt":
            server.terminate()  # Hidden Windows processes have no console for a safe CTRL_C_EVENT.
        else:
            server.send_signal(signal.SIGINT)
    except ProcessLookupError:
        server.wait(timeout=10)
        return {"method": "already-exited", "exitCode": server.returncode, "gracefulRequested": False}
    try:
        server.wait(timeout=15)
    except subprocess.TimeoutExpired:
        server.kill()
        server.wait(timeout=10)
        method += "+kill"
    return {"method": method, "exitCode": server.returncode, "gracefulRequested": os.name != "nt"}


def log_metrics(path):
    summary = None
    busy_lines = 0
    with path.open("r", encoding="utf-8", errors="replace") as log:
        for line in log:
            lower = line.lower()
            if "database is locked" in lower or "sqlite error 5" in lower or "sqlite error 6" in lower:
                busy_lines += 1
            marker = "DistributionPerformance "
            if marker in line:
                try:
                    value = json.loads(line.split(marker, 1)[1])
                    # The aggregate contract contains numeric counters only, never identities or paths.
                    accepted=sanitize(value)
                    if accepted is not None:summary=accepted
                except ValueError:
                    pass
    return {"busyErrorLogLines": busy_lines, "gracefulShutdownAggregate": summary,
            "nativeSqliteWaitMs": None}


def markdown(report):
    lines = ["# Supplemental distribution API benchmark", "", report["transport"], "",
             "20 requests per persistent connection: alternating signed catalog and a bounded file Range.",
             "All connections use the synthetic bench identity; this measures concurrent connections, not distinct PC policy sets.",
             "One warmup + three measured runs; OS cache is not flushed. HTTP timings include response-body reads.",
             "", "| Clients | Workload | Requests | Errors | Median p50 ms | Median p95 ms |",
             "|---:|---|---:|---:|---:|---:|"]
    for clients in CLIENT_COUNTS:
        rows = [row for row in report["runs"] if row["clients"] == clients and not row["warmup"]]
        for workload in ("mixed", "catalog", "range"):
            values = [row if workload == "mixed" else row["workloads"][workload] for row in rows]
            lines.append(f"| {clients} | {workload} | {sum(v['requests'] for v in values)} | {sum(v['errors'] for v in values)} | "
                         f"{statistics.median(v['p50Ms'] for v in values):.2f} | {statistics.median(v['p95Ms'] for v in values):.2f} |")
    lines += ["", "Baseline HTTP/transport errors are reported, not hidden or converted to successful latency samples.",
              "JSON contains status counts and success-only percentiles; duplicate catalog sequences count as errors. Signature shape is checked; cryptographic",
              "verification belongs to the primary launcher benchmark and automated server tests.",
              "The input package/policy/config files are unchanged; normal server token/sequence/schema writes affect only the synthetic DB.",
              "SQLite native busy duration is unavailable (null, not zero). Graceful aggregate is unavailable after Windows termination.",
              "No company server, real UE, nginx/HTTPS, WAN, cold-cache or production-SLA claim is made."]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True, type=Path, help="Published server executable")
    parser.add_argument("--config", required=True, type=Path, help="Completed synthetic benchmark server.json")
    parser.add_argument("--output", required=True, type=Path, help="New result directory; must not exist")
    parser.add_argument('--diagnostic-tool', type=Path, help='Optional dotnet-counters; profiled timings are not mixed with unprofiled baselines')
    args = parser.parse_args()
    executable = args.server.resolve(strict=True)
    config, selection = validate_fixture(args.config, executable)
    output = args.output.resolve()
    fixture = args.config.resolve(strict=True).parent
    if output == fixture or fixture in output.parents:
        parser.error("Supplemental output must be outside the existing fixture directory")
    output.mkdir(parents=True, exist_ok=False)
    claim(output)
    port = free_port()
    config = {**config, "listenUrl": f"http://127.0.0.1:{port}", "publicUrl": f"http://127.0.0.1:{port}"}
    copied_config = output / "server.json"
    write_json(copied_config, config)
    command = [str(executable), "--config", str(copied_config)]
    process_flags = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
    if config.get("authenticationMode") != "request-signature-v1":
        raise ValueError("Historical HTTP/Bearer fixtures are not supported by the hardened server; create a fresh signed fixture")
    report = {
        "schemaVersion": 1, "complete": False, **provenance({'server': executable}),
        "benchmark": "distribution-api-mixed", "warmups": 1, "measuredRuns": MEASURED_RUNS,
        "transport": "direct loopback HTTP request-signature-v1; independent signature verification",
        "platform": "windows" if os.name == "nt" else "unix", "cacheCondition": "warm process; OS cache not flushed",
        "fixtureReleaseId": selection["releaseId"], "rangeBytes": selection["rangeSize"],
        "authorizationIdentities": 1,
        "requestsPerClient": REQUESTS_PER_CLIENT, "clientCounts": list(CLIENT_COUNTS), "runs": [],
        "serverShutdown": None, "serverMetrics": None,
    }
    log_path = output / "server.log"
    counters = None
    atomic(output / 'results.json', report)
    with log_path.open("w", encoding="utf-8") as log:
        server = subprocess.Popen([*command, "serve"], stdout=log, stderr=subprocess.STDOUT, **process_flags)
        try:
            await_server(server, port)
            if args.diagnostic_tool:
                from benchmark_runtime_counters import Counters
                counters = Counters(args.diagnostic_tool, server.pid, output)
            import shutil
            shutil.copy2(fixture / "public.pem", output / "public.pem")
            payload = contained(Path(config["root"]) / "releases" / selection["releaseId"] / "files" / selection["filePath"].split("/files/", 1)[1], Path(config["root"]))
            with payload.open("rb") as stream:
                expected = stream.read(3)
            def register(path):
                outcome = subprocess.run([*command, "client-key", "add", "--client", "bench", "--public-key", str(path)],
                                         capture_output=True, timeout=60, **process_flags)
                if outcome.returncode:
                    raise RuntimeError("Synthetic load key registration failed")
            report["signedLoad"] = run_load(config["publicUrl"], output, register,
                                           "windows-x64" if os.name == "nt" else "linux-x64", server.pid,
                                           selection["filePath"], expected)
        finally:
            if counters is not None:
                report['runtimeProfiling'] = counters.finish()
            report["serverShutdown"] = stop_server(server)
            report["serverMetrics"] = log_metrics(log_path)
            atomic(output / 'results.json', report)
    report["serverMetrics"] = log_metrics(log_path)
    report['complete'] = True
    atomic(output / "results.json", report)
    (output / "results.md").write_text("# Signed API comparison\n\nSee results.json signedLoad. Historical HTTP/Bearer samples are not a comparable baseline.\n", encoding="utf-8")
    print("Report: " + str(output / "results.json"), flush=True)


if __name__ == "__main__":
    main()
