#!/usr/bin/env python3
"""Published-server intake workers 1 vs 2, with concurrent catalog/Range traffic.

Python 3.11+ and psutil are required. The supplied fixture directory must contain
release.json and its deterministic ZIP. All work uses fresh child server roots;
the source fixture is read-only. One warmup plus three measured runs per setting
are the default. No operating-system cache flush is attempted.

This is synthetic, loopback-only HTTP. Bearer authorization and signed release
publication remain enabled; this is not a production HTTPS/UE/RHEL benchmark.
Generated data is retained for inspection. Never use a company server directory.
"""
import argparse
from contextlib import closing
import concurrent.futures
import contextlib
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import statistics
import subprocess
import threading
import time
import urllib.parse
import zipfile

import psutil


JOBS = 4
CLIENTS = 10


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))]


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def load_fixture(directory):
    directory = directory.resolve(strict=True)
    metadata = json.loads((directory / "release.json").read_text(encoding="utf-8-sig"))
    package_name = metadata["packageFile"]
    if not isinstance(package_name, str) or any(char in package_name for char in "/\\:"):
        raise ValueError("Fixture packageFile must be a local ZIP filename")
    package = directory / package_name
    if package.is_symlink() or package.resolve(strict=True).parent != directory:
        raise ValueError("Fixture ZIP must be an ordinary file directly inside the fixture directory")
    with package.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if package.stat().st_size != metadata["packageSize"] or digest.lower() != metadata["packageSha256"].lower():
        raise ValueError("Fixture ZIP does not match release.json size/SHA-256")
    prefix = "" if metadata.get("payloadRoot", ".") == "." else metadata["payloadRoot"].replace("\\", "/").rstrip("/") + "/"
    with zipfile.ZipFile(package) as archive:
        files = [entry for entry in archive.infolist()
                 if not entry.is_dir() and entry.file_size > 0 and entry.filename.startswith(prefix)]
        probe = min(files, key=lambda entry: (entry.file_size, entry.filename))
        relative = probe.filename[len(prefix):]
        if not relative or any(part in ("", ".", "..") for part in relative.split("/")):
            raise ValueError("Fixture probe path is unsafe")
        probe_size = probe.file_size
    return package, metadata, relative, probe_size


def cli(command, log_path, env, secret=False):
    """Never write token-issue stdout or command lines to logs/reports."""
    result = subprocess.run([str(part) for part in command], stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env, timeout=300,
                            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if not secret:
        log_path.write_bytes(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError("Published CLI command failed; inspect the run's non-secret logs")
    return result.stdout.decode("utf-8-sig").strip()


def prepare_upload(incoming, name, package, metadata, version, platform, held):
    upload = incoming / name
    upload.mkdir(parents=True, exist_ok=False)
    value = {**metadata, "projectId": "bench-intake", "displayName": "Synthetic intake benchmark",
             "version": version, "environment": "prod", "channel": "stable", "platform": platform}
    suffix = ".uploading" if held else ""
    shutil.copyfile(package, upload / (package.name + suffix))
    write_json(upload / ("release.json" + suffix), value)
    return upload


def release_upload(upload, package_name):
    # The server ignores files ending in .uploading. Publish metadata last.
    # Renaming a directory ending in .uploading alone would NOT hold ingestion.
    (upload / (package_name + ".uploading")).rename(upload / package_name)
    (upload / "release.json.uploading").rename(upload / "release.json")


def open_readonly_database(path):
    connection = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True,
                                 isolation_level=None, timeout=.05)
    connection.execute("PRAGMA query_only=ON")
    return connection


def intake_states(connection, sources):
    markers = ",".join("?" for _ in sources)
    # Exact generated source paths, not all jobs: the prepublished seed is excluded.
    return dict(connection.execute(f"SELECT source,state FROM jobs WHERE source IN ({markers})",
                                   [str(source.resolve()) for source in sources]).fetchall())


def wait_http_ready(server, server_port, token, deadline):
    while time.monotonic() < deadline:
        if server.poll() is not None:
            raise RuntimeError("Published server exited before HTTP readiness")
        connection = http.client.HTTPConnection("127.0.0.1", server_port, timeout=1)
        try:
            connection.request("GET", "/api/v1/catalog", headers={"Authorization": "Bearer " + token})
            response = connection.getresponse()
            response.read()
            if response.status == 200:
                return
        except (OSError, http.client.HTTPException):
            pass
        finally:
            connection.close()
        time.sleep(.1)
    raise TimeoutError("HTTP readiness timed out")


class ProcessSampler:
    def __init__(self, pid):
        self.process = psutil.Process(pid)
        self.stop = threading.Event()
        times = self.process.cpu_times()
        self.initial_cpu = times.user + times.system
        self.cpu_seconds = 0.0
        self.peak_rss = 0
        self.samples = 0
        self.thread = threading.Thread(target=self._loop, daemon=True)

    def _sample(self):
        try:
            self.peak_rss = max(self.peak_rss, self.process.memory_info().rss)
            times = self.process.cpu_times()
            self.cpu_seconds = max(self.cpu_seconds, times.user + times.system - self.initial_cpu)
            self.samples += 1
        except psutil.NoSuchProcess:
            pass

    def _loop(self):
        while not self.stop.is_set():
            self._sample()
            self.stop.wait(.02)

    def start(self):
        self.thread.start()

    def finish(self):
        self.stop.set()
        self.thread.join(timeout=2)
        self._sample()
        return {"cpuSecondsSampled": self.cpu_seconds, "peakRssBytesSampled": self.peak_rss,
                "resourceSamples": self.samples, "sampleIntervalMs": 20}


def traffic_worker(server_port, token, file_path, range_bytes, start, stop, ready, pause):
    samples = []
    connection = http.client.HTTPConnection("127.0.0.1", server_port, timeout=10)
    ready.wait(timeout=30)
    start.wait()
    try:
        index = 0
        while not stop.is_set():
            kind = "catalog" if index % 2 == 0 else "range"
            path = "/api/v1/catalog" if kind == "catalog" else file_path
            headers = {"Authorization": "Bearer " + token}
            if kind == "range":
                headers["Range"] = f"bytes=0-{range_bytes - 1}"
            began = time.perf_counter()
            status, valid = 0, False
            try:
                connection.request("GET", path, headers=headers)
                response = connection.getresponse()
                body = response.read()
                status = response.status
                valid = status == 200 if kind == "catalog" else (
                    status == 206 and len(body) == range_bytes
                    and (response.getheader("Content-Range") or "").startswith(f"bytes 0-{range_bytes - 1}/"))
                if kind == "catalog" and valid:
                    envelope = json.loads(body)
                    valid = isinstance(envelope.get("payload"), str) and isinstance(envelope.get("signatureDocument"), str)
            except (OSError, http.client.HTTPException, ValueError):
                connection.close()
                connection = http.client.HTTPConnection("127.0.0.1", server_port, timeout=10)
            samples.append({"kind": kind, "milliseconds": (time.perf_counter() - began) * 1000,
                            "status": status, "ok": valid})
            index += 1
            stop.wait(pause)
    finally:
        connection.close()
    return samples


def traffic_summary(samples):
    result = {}
    for kind in ("catalog", "range", "all"):
        selected = [item for item in samples if kind == "all" or item["kind"] == kind]
        result[kind] = {"requests": len(selected), "errors": sum(not item["ok"] for item in selected),
                        "p50Ms": percentile([item["milliseconds"] for item in selected], .5),
                        "p95Ms": percentile([item["milliseconds"] for item in selected], .95)}
    return result


def stop_owned_process(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=10)


def measure_run(args, root, workers, iteration, package, metadata, probe, probe_size, platform):
    run_root = root / f"workers-{workers}-run-{iteration}"
    run_root.mkdir(exist_ok=False)
    server_root = run_root / "server"
    server_port = free_port()
    base = f"http://127.0.0.1:{server_port}"
    env = {**os.environ, "UE_DT_AGENT_DATA_ROOT": str(run_root / "credentials")}
    cli([args.launcher, "generate-signing-key", "--private-key", run_root / "key.pem",
         "--public-key", run_root / "public.pem"], run_root / "keys.log", env)
    write_json(run_root / "policy.json", {"clients": [{"id": "bench", "addresses": ["127.0.0.1"],
               "grants": [{"projectId": "bench-intake", "environment": "prod", "channel": "stable", "versions": []}]}]})
    write_json(run_root / "server.json", {"root": str(server_root), "publicUrl": base,
               "listenUrl": base, "signingKeyPath": str(run_root / "key.pem"),
               "policyPath": str(run_root / "policy.json"), "intakeWorkers": workers})
    command = [args.server, "--config", run_root / "server.json"]
    incoming = server_root / "incoming"
    seed = prepare_upload(incoming, "seed", package, metadata, "0.0.0", platform, held=False)
    job = json.loads(cli([*command, "ingest", seed], run_root / "seed-ingest.json", env))
    if job.get("state") != "pending":
        raise RuntimeError("Seed intake did not become pending")
    cli([*command, "approve", job["id"]], run_root / "seed-approve.json", env)
    token = cli([*command, "token-issue", "bench"], run_root / "unused-token-log", env, secret=True)
    if not token or any(character.isspace() for character in token):
        raise RuntimeError("Token issuance did not return one token")
    sources = [prepare_upload(incoming, f"job-{index}", package, metadata, f"1.0.{index}", platform, held=True)
               for index in range(JOBS)]
    release = f"bench-intake/prod/stable/0.0.0/{platform}"
    file_path = "/releases/" + release + "/files/" + urllib.parse.quote(probe, safe="/")
    server = None
    sampler = None
    start, stop = threading.Event(), threading.Event()
    ready = threading.Barrier(CLIENTS + 1)
    result = {"workers": workers, "iteration": iteration, "warmup": iteration == 0, "jobs": JOBS,
              "clients": CLIENTS, "completedJobs": 0, "observerSqliteBusyRetries": 0}
    with (run_root / "server.log").open("w", encoding="utf-8") as log:
        try:
            server = subprocess.Popen([str(part) for part in [*command, "serve"]], stdout=log,
                                      stderr=subprocess.STDOUT, env=env,
                                      creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            wait_http_ready(server, server_port, token, time.monotonic() + 30)
            with contextlib.closing(open_readonly_database(server_root / "distribution.db")) as database:
                with concurrent.futures.ThreadPoolExecutor(max_workers=CLIENTS) as pool:
                    futures = [pool.submit(traffic_worker, server_port, token, file_path, min(4096, probe_size),
                                           start, stop, ready, args.request_pause_ms / 1000) for _ in range(CLIENTS)]
                    try:
                        ready.wait(timeout=30)
                        sampler = ProcessSampler(server.pid)
                        sampler.start()
                        began = time.perf_counter()
                        start.set()
                        for source in sources:
                            release_upload(source, package.name)
                        deadline = time.monotonic() + args.timeout_seconds
                        while time.monotonic() < deadline:
                            if server.poll() is not None:
                                raise RuntimeError("Published server exited during intake")
                            try:
                                states = intake_states(database, sources)
                            except sqlite3.OperationalError as error:
                                if getattr(error, "sqlite_errorcode", None) not in (sqlite3.SQLITE_BUSY, sqlite3.SQLITE_LOCKED):
                                    raise
                                result["observerSqliteBusyRetries"] += 1
                                time.sleep(.05)
                                continue
                            result["completedJobs"] = sum(state == "pending" for state in states.values())
                            if any(state in ("failed", "rejected") for state in states.values()):
                                raise RuntimeError("At least one measured intake job failed; inspect retained run data")
                            if result["completedJobs"] == JOBS:
                                break
                            time.sleep(.05)
                        if result["completedJobs"] != JOBS:
                            raise TimeoutError("Timed out waiting for four pending intake jobs")
                        result["wallMs"] = (time.perf_counter() - began) * 1000
                    finally:
                        stop.set()
                        start.set()  # Release waiting traffic workers on any preparation failure.
                        if sampler is not None:
                            result.update(sampler.finish())
                    samples = [sample for future in futures for sample in future.result()]
                    result["traffic"] = traffic_summary(samples)
                    result["trafficJoinOverheadMs"] = max(0, (time.perf_counter() - began) * 1000 - result["wallMs"])
        finally:
            stop.set()
            start.set()
            stop_owned_process(server)
    result["payloadZipBytesPerJob"] = package.stat().st_size
    result["rangeBytes"] = min(4096, probe_size)
    result["probeFullFileBytes"] = probe_size
    write_json(run_root / "measurement.json", result)
    return result


def aggregate(measurements, workers):
    rows = [row for row in measurements if row["workers"] == workers and not row["warmup"]]
    if not rows:
        return None
    def median_available(kind):
        values = [row["traffic"][kind]["p95Ms"] for row in rows if row["traffic"][kind]["p95Ms"] is not None]
        return statistics.median(values) if values else None
    return {"workers": workers, "samples": len(rows),
            "medianWallMs": statistics.median(row["wallMs"] for row in rows),
            "medianCpuSecondsSampled": statistics.median(row["cpuSecondsSampled"] for row in rows),
            "maxPeakRssBytesSampled": max(row["peakRssBytesSampled"] for row in rows),
            "medianCatalogP95Ms": median_available("catalog"),
            "medianRangeP95Ms": median_available("range"),
            "errors": sum(row["traffic"]["all"]["errors"] for row in rows),
            "observerSqliteBusyRetries": sum(row["observerSqliteBusyRetries"] for row in rows)}


def save_report(root, report):
    report["aggregate"] = [value for workers in report["settings"]["workers"]
                           if (value := aggregate(report["measurements"], workers)) is not None]
    write_json(root / "results.json", report)
    lines = ["# Synthetic intake worker benchmark", "", report["transport"], "", report["cacheCondition"], "",
             "Four identical ZIPs with distinct external release versions per fresh server root; one prepublished seed.",
             "Ten concurrent clients alternate signed-catalog reads and authenticated Range reads during intake.", "",
             "| Workers | Samples | Median intake ms | CPU sec (median) | Peak RSS MiB | Catalog p95 ms | Range p95 ms | HTTP errors |",
             "|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for row in report["aggregate"]:
        catalog_p95 = "n/a" if row["medianCatalogP95Ms"] is None else f"{row['medianCatalogP95Ms']:.1f}"
        range_p95 = "n/a" if row["medianRangeP95Ms"] is None else f"{row['medianRangeP95Ms']:.1f}"
        lines.append(f"| {row['workers']} | {row['samples']} | {row['medianWallMs']:.1f} | {row['medianCpuSecondsSampled']:.3f} | "
                     f"{row['maxPeakRssBytesSampled'] / 1048576:.1f} | {catalog_p95} | {range_p95} | {row['errors']} |")
    lines += ["", "Timing starts before .uploading file renames and ends when all four exact jobs are pending.",
              "Watcher scan latency (up to its 2-second interval), hash/copy/extraction and concurrent HTTP load are included.",
              "Seed publication, fixture copying, signing-key/token setup and process startup are excluded.",
              "CPU/RSS use 20-ms samples of the server process only; API p95 includes failed requests and in-flight completions.",
              "SQLite observation is read-only with a 50-ms poll/busy timeout; observer retries are not server busy duration.",
              "Workers are configured values; older published binaries may ignore intakeWorkers. Do not label those as a 1-vs-2 comparison.",
              "Warmup results are retained but excluded above. Generated roots are retained; no existing installations are touched.",
              "No real UE, RHEL, WAN, cold-OS-cache or production SLA claim is made."]
    if not report.get("complete"):
        lines += ["", "Run is incomplete; only completed measurements are shown."]
    (root / "results.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--server", required=True, type=Path)
    parser.add_argument("--launcher", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="Must not already exist")
    parser.add_argument("--workers", default="1,2", help="1,2 by default; use 1 alone for an old binary that ignores the setting")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--timeout-seconds", type=float, default=600)
    parser.add_argument("--request-pause-ms", type=float, default=10)
    args = parser.parse_args()
    try:
        workers = [int(value) for value in args.workers.split(",")]
    except ValueError:
        parser.error("--workers must be 1, 2 or 1,2")
    if not workers or len(set(workers)) != len(workers) or any(value not in (1, 2) for value in workers):
        parser.error("--workers must be 1, 2 or 1,2")
    if args.runs < 1 or args.timeout_seconds <= 0 or args.request_pause_ms < 0:
        parser.error("Runs/timeout must be positive and request pause nonnegative")
    args.server = args.server.resolve(strict=True)
    args.launcher = args.launcher.resolve(strict=True)
    package, metadata, probe, probe_size = load_fixture(args.fixture)
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=False)
    platform = "windows-x64" if os.name == "nt" else "linux-x64"
    report = {"schemaVersion": 1, "complete": False, "platform": platform, "warmups": 1, "runs": args.runs,
              "transport": "Loopback HTTP only; signed release publication and Bearer authorization enabled.",
              "cacheCondition": "Fresh server root per iteration; OS cache is not flushed; fixture ZIP content is identical.",
              "settings": {"workers": workers, "jobs": JOBS, "clients": CLIENTS, "requestPauseMs": args.request_pause_ms},
              "fixtureZipBytes": package.stat().st_size, "measurements": []}
    save_report(root, report)
    try:
        # Alternate settings within each iteration to reduce fixed-order OS-cache bias.
        for iteration in range(args.runs + 1):
            for worker_count in workers if iteration % 2 == 0 else list(reversed(workers)):
                measurement = measure_run(args, root, worker_count, iteration, package, metadata, probe, probe_size, platform)
                report["measurements"].append(measurement)
                save_report(root, report)
                print(f"workers={worker_count} iteration={iteration} warmup={iteration == 0}: "
                      f"intake={measurement['wallMs']:.1f}ms errors={measurement['traffic']['all']['errors']}", flush=True)
        report["complete"] = True
    except Exception as error:
        report["failureType"] = type(error).__name__  # No tokens, user paths or raw command lines in reports.
        raise RuntimeError("Intake benchmark failed; inspect retained isolated run logs") from None
    finally:
        save_report(root, report)
    print("Wrote results.json and results.md inside the requested fresh output directory.", flush=True)


if __name__ == "__main__":
    main()
