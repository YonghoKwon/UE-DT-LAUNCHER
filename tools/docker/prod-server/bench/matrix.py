"""Run PROD-02D in a fresh, operator-reserved Docker Desktop measurement window."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import ipaddress
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

BENCH = Path(__file__).resolve().parent
TOOLS = BENCH.parents[2]
sys.path.insert(0, str(TOOLS))
from prepare_prod_server import (
    Fixture, ENTRY_HOME, KEYS, ORIGIN, ROOT, TEMPLATES, published, run, digest, inventory as file_inventory,
)

ABORT = 3_000_000_000
SUBNET = ipaddress.ip_network("172.30.229.0/24")
PROJECTS = ("prod-02d-20gb", "prod-02d-1gb", "m7at10-dt")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path, content):
    path.write_text(json.dumps(content, indent=2) + "\n", encoding="utf-8")


def mount(source, target, read_only=True, volume=False):
    kind = "volume" if volume else "bind"
    return ["--mount", f"type={kind},source={source},target={target}" + (",readonly" if read_only else "")]


def inventory():
    selections = {
        "containers": ("ps", "-a", "--no-trunc", "--format", "{{.ID}} {{.Names}}"),
        "networks": ("network", "ls", "--no-trunc", "--format", "{{.ID}} {{.Name}}"),
        "volumes": ("volume", "ls", "--format", "{{.Name}}"),
        "images": ("images", "--no-trunc", "--format", "{{.ID}} {{.Repository}}:{{.Tag}}"),
    }
    return {key: sorted(run("docker", *args).splitlines()) for key, args in selections.items()}


class Matrix(Fixture):
    def __init__(self, args):
        root = args.work.resolve(strict=True)
        args.work = root / "fixture"
        super().__init__(args)
        self.root = root
        self.evidence = args.evidence.resolve(strict=True)
        self.ue = args.ue.resolve(strict=True)
        self.launcher = published(load(root / "win" / "cohort.json"), "launcher")
        self.game = Path(load(root / "win" / "cohort.json")["fixtureAssets"]["synthetic"]["path"])
        self.static_volume = self.project + "_bench-static"
        self.network = self.project + "_default"
        self.m0 = False
        self.sim = {}
        self.results = {}
        os.environ.update(UEDT_BENCH_SUBNET=str(SUBNET), UEDT_BENCH_NGINX=str(root / "nginx.conf"),
                          UEDT_BENCH_M0_NGINX=str(root / "nginx-m0.conf"))

    def volume_names(self):
        return super().volume_names() | {self.static_volume}

    def compose(self, *args, **kwargs):
        volumes = self.guard_docker()
        required = [self.project + "_" + role for role in ("store", "config", "keys")]
        if self.m0:
            required.append(self.static_volume)
        if args[0] in {"up", "run", "exec", "cp", "start", "restart"} and any(
                name not in volumes for name in required):
            raise RuntimeError("Refusing Compose operation with a missing owned volume")
        files = (TEMPLATES / "compose.yaml", BENCH / "compose.bench.yaml")
        if self.m0:
            files += (BENCH / "compose.m0.yaml",)
        options = [item for path in files for item in ("-f", path)]
        return run("docker", "compose", "-p", self.project, *options, *args, **kwargs)

    def one_shot(self, *args, mounts=(), image=None, entrypoint=None, **kwargs):
        entry = ("--entrypoint", entrypoint) if entrypoint else ()
        return run("docker", "run", "--rm", "--network", "none", *self.docker_labels(),
                   *mounts, *entry, image or self.sim_image, *args, **kwargs)

    def bench_config(self):
        nginx = (TEMPLATES / "nginx.conf").read_text(encoding="utf-8")
        log_fields = "$sent_http_content_range"
        if nginx.count(log_fields) != 1 or nginx.count("        location /") != 1:
            raise RuntimeError("Unexpected 01D nginx configuration")
        nginx = nginx.replace(log_fields, log_fields +
                              " $http_range $body_bytes_sent $request_time $upstream_status $upstream_header_time")
        (self.root / "nginx.conf").write_text(nginx, encoding="utf-8")
        static = """        location ^~ /bench-static/ {
            if ($server_port != 18080) { return 404; }
            alias /bench-static/;
            autoindex off;
        }
"""
        (self.root / "nginx-m0.conf").write_text(
            nginx.replace("        location /", static + "        location /"), encoding="utf-8")

    def prepare(self):
        self.bench_config()
        self.stage(load(self.root / "linux" / "cohort.json"), None)
        self.up(self.root / "server-multi")
        self.create_volume("bench-static")
        static_mount = mount(self.static_volume, "/bench-static", False, volume=True)
        identifier = load(self.root / "win" / "cohort.json")["fixtureAssets"]["synthetic"]
        if digest(self.game) != identifier["sha256"]:
            raise RuntimeError("Unexpected synthetic entry executable")
        sim_context = self.work / "sim-context"
        # Reuse the 01D pinned Python/cryptography image and the unmodified 02T driver.
        shutil.copy2(BENCH / "static_client.py", sim_context / "static_client.py")
        with (sim_context / "Dockerfile").open("a", encoding="utf-8") as dockerfile:
            dockerfile.write("\nCOPY static_client.py /static_client.py\n")
        run("docker", "build", "--platform", "linux/amd64", "--build-arg", f"RUN_ID={self.run_id}",
            "--build-arg", f"OWNER_TOKEN={self.ownership['owner']}", "-t", self.sim_image, sim_context, timeout=900)
        self.disk_guard(20_000_000_000)
        self.payloads = json.loads(self.one_shot(
            "python", "/tool.py", "/bench-static",
            mounts=static_mount + mount(BENCH / "payload.py", "/tool.py"), timeout=900))
        save(self.evidence / "payloads.json", self.payloads)
        for project in PROJECTS:
            self.publish_release(project, mount(self.static_volume, "/bench-static", volume=True))
        self.register_clients()

    def publish_release(self, project, static_mount, before=None, existing_shape=None):
        if before is None:
            before = self.sample("before")[0]
        incoming = f"{ROOT}/incoming/{project}"
        store = mount(self.project + "_store", ROOT, False, volume=True)
        self.require_volume(self.project + "_store")
        membership = mount(self.ue, "/ue") if project == "m7at10-dt" else mount(self.game, "/game.exe")
        shape = existing_shape or json.loads(self.one_shot(
            "python", "/tool.py", project, incoming,
            mounts=store + static_mount + membership + mount(BENCH / "pack.py", "/tool.py"), timeout=900))
        entry = "m7at10_dt.exe" if project == "m7at10-dt" else "game.exe"
        self.one_shot("release-metadata", "--zip", f"{incoming}/payload.zip",
                      "--project-id", project, "--version", "1.0.0", "--platform", "windows-x64",
                      "--entry-point", entry, "--executable-paths", entry, "--output", f"{incoming}/release.json",
                      mounts=store, image=self.image, entrypoint="/dt/publish/linux-x64/UeDtLauncher", timeout=900)
        self.dist("chown", "-R", "uedt-distribution:uedt-upload", incoming)
        uploaded = self.sample("uploaded")[0]
        job = self.intake(incoming)
        pending = self.sample("pending")[0]
        approving = self.sample("approving", job)
        published_sample = self.sample("published")[0]
        if not approving or json.loads(self.server("inspect", job))["state"] != "published":
            raise RuntimeError(f"Approval evidence missing for {project}")
        self.promote(project, "windows-x64")
        promoted = self.sample("promoted")[0]
        baseline = sum(before.values())
        steady = sum(published_sample.values()) - baseline
        peak = max(map(lambda row: sum(row.values()), [uploaded, pending, *approving, published_sample, promoted])) - baseline
        save(self.evidence / (project + "-storage.json"), shape | {
            "job": job, "before": before, "uploaded": uploaded, "pending": pending, "approving": approving,
            "published": published_sample, "promoted": promoted, "k": steady / shape["payloadBytes"],
            "peakMultiplier": peak / shape["payloadBytes"], "steadyBytes": steady, "peakBytes": peak})

    def register_clients(self):
        self.dist_ip = self.container_address(self.compose("ps", "-q", "dist"), self.network)
        public = self.root / "release-public.pem"
        self.compose("cp", f"dist:{KEYS}/release-public.pem", public)
        (self.root / "private").mkdir()
        os.environ["UE_DT_AGENT_DATA_ROOT"] = str(self.root / "private")
        self.credential = f"prod-{self.run_id}-win"
        device = self.root / "windows-public.json"
        run(self.launcher, "credential", "keygen", "--name", self.credential, "--key-id", self.credential,
            "--public-out", device, "--storage", "portable", sensitive=True)
        code = self.http_status("/api/v1/catalog")
        if code != "401":
            raise RuntimeError(f"Expected Windows 401 readiness, got {code}")
        clients = [("windows", self.nginx_peer(), device, PROJECTS)]
        for slot in range(1, 11):
            name = f"{self.project}-sim-{slot}"
            address = str(SUBNET[100 + slot])
            run("docker", "run", "-d", "--init", "--name", name, *self.docker_labels(),
                "--network", self.network, "--ip", address, "--add-host", f"host.docker.internal:{self.dist_ip}",
                *mount(self.evidence / "runs", "/rounds", False),
                *mount(public, "/public.pem"), self.sim_image, "sleep", "infinity")
            if self.container_address(name, self.network) != address:
                raise RuntimeError("Simulator address mismatch")
            self.sim[slot] = name
            self.exec_sim(slot, "python", "/driver.py", "keygen", "--work-dir", "/identity", "--key-id", f"sim-{slot}",
                          sensitive=True)
            key_path = self.root / f"sim-{slot}-public.json"
            run("docker", "cp", f"{name}:/identity/device-public.json", key_path)
            clients.append((f"sim-{slot}", address, key_path, PROJECTS[:2]))
        policy = {"clients": [
            {"id": name, "addresses": [address], "grants": [
                {"projectId": project, "environment": "prod", "channel": "stable", "versions": ["1.0.0"]}
                for project in projects]} for name, address, _, projects in clients]}
        self.compose("run", "--rm", "--no-deps", "-T", "setup", "policy", input_text=json.dumps(policy))
        for name, _, key, _ in clients:
            target = f"{ENTRY_HOME}/{name}-public.json"
            self.copy_to("dist", key, target)
            self.dist("chmod", "0644", target)
            self.server("client-key", "add", "--client", name, "--public-key", target)
        save(self.evidence / "paths.json", {"signedOrigin": ORIGIN, "directDistIp": self.dist_ip, "subnet": str(SUBNET),
             "addresses": {name: address for name, address, _, _ in clients}})

    def exec_sim(self, slot, *command, **kwargs):
        return run("docker", "exec", self.sim[slot], *command, **kwargs)

    def http_status(self, path):
        return run("curl.exe", "--noproxy", "*", "--max-time", "15", "-sS", "-o", "NUL",
                   "-w", "%{http_code}", ORIGIN + path)

    def disk_guard(self, size):
        df = self.dist("df", "-B1", "--output=avail", ROOT)
        available = int(df.splitlines()[-1])
        if available < size * 2:
            raise RuntimeError(f"VM disk available {available} B is below {size * 2} B")

    def verify_measurement_alone(self):
        # Fixture-owned workers are allowed. Capture only process names and PIDs.
        owned = set(run("docker", "ps", "-q", "--filter", f"label=uedt.run={self.run_id}").splitlines())
        active = set(run("docker", "ps", "-q").splitlines())
        builds = run("docker", "buildx", "history", "ls", "--format", "{{.Status}}")
        if active - owned or "running" in builds.lower():
            raise RuntimeError("Another Docker container or build is running")
        process_check = (
            "Get-CimInstance Win32_Process | Where-Object { "
            "$_.Name -match '^(testhost|MSBuild|vstest.console|UnrealEditor|UnrealBuildTool)\\.exe$' -or "
            "($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match "
            "'\\s(test|build|publish|vstest)\\s|MSBuild\\.dll(?!.*nodemode)') "
            "} | ForEach-Object { '{0} {1}' -f $_.ProcessId,$_.Name }"
        )
        jobs = run("powershell", "-NoProfile", "-Command", process_check)
        if jobs:
            raise RuntimeError("Another build/test or Unreal workload is running: " + jobs)

    def backend_pid(self):
        dist = self.compose("ps", "-q", "dist")
        result = run("docker", "top", dist, "-eo", "pid,args")
        matching = [row.split()[0] for row in result.splitlines()
                    if "/UeDtLauncher.DistributionServer serve --config " in row]
        pid, = matching
        return pid

    def snapshot(self):
        return {"distDf": self.dist("df", "-B1", ROOT), "simDf": self.exec_sim(1, "df", "-B1", "/"),
                "dockerDf": run("docker", "system", "df"), "hostCFreeBytes": shutil.disk_usage("C:\\").free}

    @contextmanager
    def round(self, name, size):
        self.disk_guard(size)
        self.verify_measurement_alone()
        output = self.evidence / "runs" / name
        output.mkdir()
        print(f"{name} started", flush=True)
        save(output / "before.json", self.snapshot())
        with (output / "resources.jsonl").open("wb") as samples, (output / "observer.log").open("wb") as log:
            observer = subprocess.Popen(["docker", "run", "--rm", "--network", "none", "--pid", "host",
                                         *self.docker_labels(), *mount(output, "/round"),
                                         *mount(BENCH / "observe.py", "/observe.py"),
                                         self.sim_image, "python", "/observe.py", self.backend_pid()],
                                        stdout=samples, stderr=log)
            try:
                deadline = time.monotonic() + 30
                while (output / "resources.jsonl").stat().st_size == 0:
                    if observer.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Docker VM sampler failed to start")
                    time.sleep(.05)
                yield output
            finally:
                (output / "observe.stop").touch()
                if observer.wait(timeout=30):
                    raise RuntimeError("Docker VM sampler failed")
                save(output / "after.json", self.snapshot())
        self.verify_measurement_alone()
        from summarize import environment
        self.results[name] = environment(output, size)
        save(output / "environment.json", self.results[name])
        print(f"{name} recorded; environment limited={self.results[name]['limited']}", flush=True)

    def simulator_round(self, variant, payload, participants, repetition):
        group = f"{variant}-{payload}-n{participants}-{repetition}"
        fixture = self.payloads[payload]
        access_start = len(self.nginx_access()) if variant == "M2" else None
        with self.round(group, fixture["bytes"]) as output:
            def download(slot):
                report = f"/rounds/{group}/slot-{slot}.json"
                if variant == "M0":
                    args = ("python", "/static_client.py", "--name", payload, "--size", str(fixture["bytes"]),
                            "--sha256", fixture["sha256"], "--group", group)
                else:
                    args = ("python", "/driver.py", "download", "--origin", ORIGIN, "--work-dir", "/identity",
                            "--key-id", f"sim-{slot}", "--signing-public-key", "/public.pem",
                            "--signing-key-id", "prod-01d-release", "--project", "prod-02d-" + payload,
                            "--platform", "windows-x64", "--version", "1.0.0", "--path", "payload.bin",
                            "--barrier-dir", f"/rounds/{group}", "--barrier-group", group, "--timeout", "120")
                    if variant == "M1" and repetition:
                        args += ("--abort-after-bytes", str(ABORT))
                command = ["docker", "exec", self.sim[slot], *args, "--participants", str(participants),
                           "--slot", str(slot), "--report", report]
                result = subprocess.run(command, capture_output=True, timeout=900)
                return {"slot": slot, "exit": result.returncode, "reportPresent": (output / Path(report).name).is_file()}
            with ThreadPoolExecutor(max_workers=participants) as executor:
                save(output / "participants.json", list(executor.map(download, range(1, participants + 1))))
        if access_start is not None:
            save(output / "access.json", self.nginx_access()[access_start:])
        return self.results[group]["limited"]

    def m0_state(self, enabled):
        self.m0 = enabled
        self.require_volume(self.static_volume)
        self.compose("up", "-d", "--no-deps", "--force-recreate", "--wait", "nginx")
        direct = self.exec_sim(1, "python", "-c",
                               "import http.client; c=http.client.HTTPConnection('host.docker.internal',18080);"
                               "c.request('HEAD','/bench-static/1gb.bin'); print(c.getresponse().status); c.close()")
        external = self.http_status("/bench-static/1gb.bin")
        save(self.evidence / ("m0-on.json" if enabled else "m0-off.json"),
             {"directStatus": int(direct), "hostStatus": int(external)})
        if external != "404" or direct != ("200" if enabled else "404"):
            raise RuntimeError("M0 static path visibility differs from the fixture contract")

    def nginx_access(self):
        access = self.compose("logs", "--no-color", "--no-log-prefix", "nginx")
        result = []
        for line in access.splitlines():
            fields = line.split()
            if (len(fields) not in (10, 11) or not fields[1].isdigit() or
                    not fields[3].startswith(("/releases/", "/api/v1/"))):
                continue
            address, status, port, path, *suffix = fields
            response_range = " ".join(suffix[:-5])
            request_range, body_bytes, elapsed, upstream, header_time = suffix[-5:]
            result.append({"peer": address, "status": int(status), "path": path,
                           "port": int(port), "upstreamStatus": upstream, "upstreamHeaderSeconds": header_time,
                           "contentRange": response_range, "range": request_range,
                           "bodyBytes": int(body_bytes), "seconds": float(elapsed)})
        return result

    def sample_config(self, folder, project, budget):
        config = folder / "launcher.json"
        run(self.launcher, "sample-config", "--deployment-mode", "portable", "--auth", "request-signature-v1",
            "--server-url", ORIGIN, "--project-id", project, "--platform", "windows-x64",
            "--credential-name", self.credential, "--signing-key-id", "prod-01d-release",
            "--public-key", self.root / "release-public.pem", "--output", config)
        document = load(config)
        document.setdefault("performance", {})["resumeCacheBytes"] = budget
        # Keep machine integration disabled for these private no-launch installations.
        document["windowsIntegration"] = {"createDesktopShortcut": False, "createStartMenuShortcut": False,
                                          "registerAppEntry": False}
        save(config, document)
        return config

    def run_product(self, config, interrupted=None):
        command = [str(self.launcher), "run", "--no-launch", "--config", str(config)]
        started = time.monotonic()
        with subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as process:
            try:
                if interrupted is None:
                    code = process.wait(timeout=1800)
                    seconds = time.monotonic() - started
                    return {"exit": code, "seconds": seconds, "performance": self.product_performance(config.parent)}
                pattern = "*.partial" if interrupted else "payload.bin.download-*"
                deadline = time.monotonic() + 300
                while process.poll() is None and time.monotonic() < deadline:
                    for candidate in config.parent.rglob(pattern):
                        try:
                            length = candidate.stat().st_size
                        except FileNotFoundError:
                            continue
                        if ABORT <= length < self.payloads["20gb"]["bytes"]:
                            process.kill()
                            exit_code = process.wait(timeout=20)
                            return {"exit": exit_code, "interrupted": True, "observedBytes": length,
                                    "retainedBytes": candidate.stat().st_size, "seconds": time.monotonic() - started}
                    time.sleep(.02)
                return {"exit": process.poll(), "interrupted": False, "seconds": time.monotonic() - started}
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=20)

    @staticmethod
    def product_performance(folder):
        prefix = "[Performance] "
        records = []
        for log in sorted((folder / "logs").glob("launcher-*.log"), key=lambda file: file.stat().st_mtime_ns):
            for line in log.read_text(encoding="utf-8-sig", errors="replace").splitlines():
                if prefix in line:
                    record = json.loads(line.split(prefix, 1)[1])
                    records.append({key: record[key] for key in ("networkBytes", "reusedBytes", "downloadMilliseconds")})
        return records[-1] if records else None

    def verify_product(self, folder, project):
        selection = Path(project, "prod", "stable", "1.0.0", "windows-x64")
        manifest = load(folder / ".state" / selection / "installed-manifest.json")
        installed = folder / "app" / selection
        manifest_inventory = {item["path"]: [item["size"], item["sha256"].lower()] for item in manifest["files"]}
        if project == "m7at10-dt":
            if manifest_inventory != load(self.evidence / "m7at10-inventory.json"):
                raise RuntimeError("Installed Manifest differs from the original m7at10 ZIP inventory")
        elif manifest_inventory.get("payload.bin") != [self.payloads["20gb"]["bytes"], self.payloads["20gb"]["sha256"]]:
            raise RuntimeError("Installed Manifest differs from the synthetic source")
        for item in manifest["files"]:
            file = (installed / item["path"]).resolve(strict=True)
            if (not file.is_relative_to(installed.resolve()) or file.stat().st_size != item["size"]
                    or digest(file) != item["sha256"].lower()):
                raise RuntimeError(f"Installed content differs from Manifest: {item['path']}")
        return {"filesVerified": len(manifest["files"]), "verifiedBytes": sum(x["size"] for x in manifest["files"])}

    def product_round(self, name, project, budget, interrupt=None):
        data_size = 7_122_065_711 if interrupt is None else self.payloads["20gb"]["bytes"]
        folder = self.root / name
        folder.mkdir()
        config = self.sample_config(folder, project, budget)
        with self.round(name, data_size) as output:
            before = len(self.nginx_access())
            first = self.run_product(config, interrupt)
            initial = self.nginx_access()[before:]
            second = self.run_product(config) if first.get("interrupted") else None
            # Only capture parsed, allowlisted access fields. Range is compared to
            # the actual bytes retained after the owned process has exited.
            final = self.nginx_access()[before + len(initial):]
            peer = load(self.evidence / "paths.json")["addresses"]["windows"]
            initial = [item for item in initial if item["peer"] == peer and item["port"] == 80]
            final = [item for item in final if item["peer"] == peer and item["port"] == 80]
            report = {"initial": first, "repeat": second, "initialHttp": initial, "repeatHttp": final,
                      "resumeBudgetBytes": budget}
            save(output / "product.json", report)
        completed = second if second is not None else first
        if completed["exit"] == 0 and (interrupt is None or first.get("interrupted")):
            report.update(self.verify_product(folder, project))
            save(output / "product.json", report)

    def measure(self):
        self.payloads = load(self.evidence / "payloads.json")
        paths = load(self.evidence / "paths.json")
        self.dist_ip = paths["directDistIp"]
        self.sim = {slot: f"{self.project}-sim-{slot}" for slot in range(1, 11)}
        hashes = {"/driver.py": digest(TOOLS / "large_download_driver.py"),
                  "/static_client.py": digest(BENCH / "static_client.py")}
        for slot, container in self.sim.items():
            instance, = json.loads(run("docker", "container", "inspect", container))
            if not self.matches_owner(instance["Config"]["Labels"]):
                raise RuntimeError(f"Unexpected simulator identity for slot {slot}")
            run("docker", "cp", BENCH / "static_client.py", f"{instance['Id']}:/static_client.py")
            implemented = self.exec_sim(slot, "sha256sum", *hashes)
            if {path: sha for sha, path in (line.split() for line in implemented.splitlines())} != hashes:
                raise RuntimeError(f"Unexpected benchmark client code in slot {slot}")
        save(self.evidence / "client-script-hashes.json", hashes)
        self.credential = f"prod-{self.run_id}-win"
        os.environ["UE_DT_AGENT_DATA_ROOT"] = str(self.root / "private")
        self.verify_measurement_alone()
        archive = f"{ROOT}/incoming/m7at10-dt/payload.zip"
        store = mount(self.project + "_store", ROOT, volume=True)
        self.require_volume(self.project + "_store")
        source = file_inventory(self.ue)
        archived = self.one_shot("python", "/tool.py", "ue-inventory", archive,
                                 mounts=store + mount(BENCH / "pack.py", "/tool.py"), timeout=900)
        if json.dumps(json.loads(archived), sort_keys=True) != json.dumps(source, sort_keys=True):
            raise RuntimeError("Original m7at10 and ZIP inventories differ")
        save(self.evidence / "m7at10-inventory.json", source)
        self.verify_measurement_alone()
        self.bench_config()
        progress = self.evidence.parent / "progress.md"
        with progress.open("a", encoding="utf-8") as stream:
            stream.write(f"\n- 측정 단독 시작 {time.strftime('%Y-%m-%dT%H:%M:%S%z')}\n")
        try:
            try:
                self.m0_state(True)
                for payload, groups in (("20gb", (1,)), ("1gb", (1, 5, 10))):
                    for participants in groups:
                        limited = [self.simulator_round("M0", payload, participants, repeat) for repeat in range(4)]
                        if any(limited):
                            save(self.evidence / f"M0-{payload}-escalation-stop.json", {"limitedAt": participants})
                            break
            finally:
                self.m0_state(False)
            for repeat in range(4):
                self.simulator_round("M1", "20gb", 1, repeat)
            for participants in (1, 5, 10):
                limited = [self.simulator_round("M2", "1gb", participants, repeat) for repeat in range(4)]
                if any(limited):
                    save(self.evidence / "M2-1gb-escalation-stop.json", {"limitedAt": participants})
                    break
            self.product_round("M3-cold", "m7at10-dt", 8_000_000_000)
            for repeat in range(1, 4):
                self.product_round(f"M4-positive-{repeat}", PROJECTS[0], 21_000_000_000, True)
            self.product_round("M4-negative-1", PROJECTS[0], 1_000_000_000, False)
        finally:
            with progress.open("a", encoding="utf-8") as stream:
                stream.write(f"- 측정 단독 종료 {time.strftime('%Y-%m-%dT%H:%M:%S%z')}\n")
        source_unchanged = source == file_inventory(self.ue)
        save(self.evidence / "m7at10-source-after.json", {"unchanged": source_unchanged, "files": len(source)})
        if not source_unchanged:
            raise RuntimeError("Original m7at10 inventory changed during testing")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("prepare", "measure", "down"))
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--ue", type=Path, required=True)
    args = parser.parse_args()
    matrix = Matrix(args)
    if args.action == "prepare":
        if run("docker", "ps", "-q"):
            raise RuntimeError("Docker is already running containers")
        (matrix.evidence / "runs").mkdir(exist_ok=True)
        save(matrix.evidence / "before-inventory.json", inventory())
        matrix.prepare()
    elif args.action == "measure":
        matrix.measure()
        cleanup(matrix)
    else:
        cleanup(matrix)


def cleanup(matrix):
    matrix.down()
    save(matrix.evidence / "after-inventory.json", inventory())
    save(matrix.evidence / "after-disk.json", {"dockerDf": run("docker", "system", "df"),
         "hostCFreeBytes": shutil.disk_usage("C:\\").free})
    shutil.copy2(matrix.work / "docker-residual.json", matrix.evidence)


if __name__ == "__main__":
    main()
