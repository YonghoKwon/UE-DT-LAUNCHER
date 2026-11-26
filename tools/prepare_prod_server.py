"""Stage the isolated PROD-01D Docker fixture and exercise the published clients."""

import argparse
import hashlib
import ipaddress
import json
import os
import re
import shutil
import socket
import subprocess
import time
import tempfile
import zipfile
from pathlib import Path

TEMPLATES = Path(__file__).resolve().parent / "docker" / "prod-server"
ORIGIN = "http://host.docker.internal:18080"
ROOT = "/dt/store"
ENTRY_HOME = "/dt/tools/server-home"
ENTRY = "/dt/tools/entrypoint.sh"
KEYS = "/dt/keys"
LINUX_CLI = "/dt/publish/linux-x64/UeDtLauncher"
PROJECT = "prod-01d-demo"
VERSION = "1.0.0"


def run(*args, sensitive=False, input_text=None, timeout=300):
    result = subprocess.run(
        [str(arg) for arg in args], input=input_text.encode("utf-8") if input_text is not None else None,
        capture_output=True, timeout=timeout,
    )
    output = result.stdout.decode("utf-8", errors="replace")
    if result.returncode:
        message = "" if sensitive else (output + result.stderr.decode("utf-8", errors="replace")).strip()
        raise RuntimeError(f"{args[0]} {args[1:3]} exit {result.returncode}: {message}")
    return "" if sensitive else output.strip()


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def published(cohort, role):
    artifact = cohort["binaries"][role]
    path = Path(artifact["path"])
    if digest(path) != artifact["sha256"]:
        raise ValueError(f"Published {role} hash differs from cohort")
    return path


class Fixture:
    def __init__(self, args):
        if not re.fullmatch(r"[a-z0-9]{8,32}", args.run_id):
            raise ValueError("Use a fresh run ID with 8–32 lowercase letters/digits")
        self.run_id = args.run_id
        self.project = f"uedt-prod-{self.run_id}"
        self.image = f"uedt-prod-test:{self.run_id}"
        self.linux = f"{self.project}-linux"
        self.simulator = f"{self.project}-simulator"
        self.sim_image = f"uedt-prod-simulator:{self.run_id}"
        self.work = args.work.resolve()
        self.context = self.work / "context"
        os.environ["UEDT_RUN_ID"] = self.run_id
        os.environ["UEDT_BUILD_CONTEXT"] = str(self.context)

    def compose(self, *args, **kwargs):
        return run("docker", "compose", "-p", self.project, "-f", TEMPLATES / "compose.yaml", *args, **kwargs)

    def dist(self, *args, **kwargs):
        return self.compose("exec", "-T", "dist", *args, **kwargs)

    def server(self, *args, **kwargs):
        return self.dist(ENTRY, "admin", *args, **kwargs)

    def client(self, *args, **kwargs):
        return run("docker", "exec", "-i", self.linux, *args, **kwargs)

    def stage(self, linux_cohort, ssh_public_key):
        if socket.gethostbyname("host.docker.internal") != "192.168.0.2":
            raise ValueError("host.docker.internal must resolve to 192.168.0.2 on Windows")
        for endpoint in [("192.168.0.2", 18080), ("127.0.0.1", 12222)]:
            with socket.socket() as probe:
                if os.name == "nt":
                    probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                probe.bind(endpoint)
        if linux_cohort["rid"] != "linux-x64":
            raise ValueError("Use a linux-x64 publication")
        if any(run("docker", *args, "--filter", f"label=uedt.run={self.run_id}") for args in (
            ("ps", "-aq"), ("network", "ls", "-q"), ("volume", "ls", "-q"), ("image", "ls", "-aq")
        )) or run("docker", "image", "ls", "-q", self.image):
            raise ValueError("Run image already exists")
        self.work.mkdir(parents=True, exist_ok=False)
        self.context.mkdir()
        for template in TEMPLATES.iterdir():
            if template.is_file() and template.name not in {"README.md", "compose.yaml"}:
                (self.context / template.name).write_bytes(template.read_bytes().replace(b"\r\n", b"\n"))
        if ssh_public_key is not None:
            key = ssh_public_key.read_bytes()
        else:
            with tempfile.TemporaryDirectory(prefix="uedt-prod-ssh-") as directory:
                target = Path(directory) / "uploader"
                run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", target, sensitive=True)
                key = target.with_suffix(".pub").read_bytes()
        if not key.startswith(b"ssh-ed25519 ") or len(key.splitlines()) != 1:
            raise ValueError("Use one Ed25519 SSH public-key line")
        (self.context / "uploader.pub").write_bytes(key.rstrip() + b"\n")
        simulator = self.work / "sim-context"
        simulator.mkdir()
        shutil.copy2(TEMPLATES / "Dockerfile.simulator", simulator / "Dockerfile")
        shutil.copy2(TEMPLATES.parent.parent / "large_download_driver.py", simulator)
        for role in ("server", "launcher"):
            source = published(linux_cohort, role)
            destination = self.context / role
            for name, artifact in linux_cohort["supportFiles"].items():
                if name.startswith(role + "/"):
                    support = Path(artifact["path"])
                    if support.parent != source.parent or digest(support) != artifact["sha256"]:
                        raise ValueError(f"Published support file differs: {name}")
            if any(path.is_symlink() or path.is_junction() for path in source.parent.rglob("*")):
                raise ValueError(f"Published {role} includes a linked artifact")
            shutil.copytree(source.parent, destination)

    def up(self, server_multi):
        self.compose("build", "dist", timeout=900)
        audited_image = run("docker", "image", "inspect", "--format", "{{.Id}}", self.image)
        audit = (TEMPLATES / "check-elf.sh").read_text(encoding="utf-8")
        elf = run("docker", "run", "--rm", "-i", "--network", "none", "--label", f"uedt.run={self.run_id}",
                  "--mount", f"type=bind,source={server_multi},target=/mnt/server-multi,readonly",
                  "--entrypoint", "bash", self.image, "-s", input_text=audit)
        (self.work / "elf-readelf.txt").write_text(elf + "\n", encoding="utf-8")
        self.compose("run", "--rm", "--no-deps", "-T", "setup", "seed")
        self.compose("up", "-d", "--build", "--wait", "--wait-timeout", "90", timeout=300)
        if audited_image != run("docker", "image", "inspect", "--format", "{{.Id}}", self.image):
            raise RuntimeError("The running image differs from the ELF-audited image")
        self.save("compose-resolved.json", json.loads(self.compose("config", "--format", "json")))
        self.save("compose-ps.json", self.ready())
        self.compose("exec", "-T", "nginx", "nginx", "-t")
        self.save("image-runtime.json", {
            "redhatRelease": self.dist("cat", "/etc/redhat-release"),
            "rpm": self.dist("rpm", "-q", "nginx", "openssh-server", "libicu", "openssl", "glibc").splitlines(),
            "pid1": self.dist("cat", "/proc/1/comm"),
            "composeSubnet": json.loads(run("docker", "network", "inspect", self.project + "_default"))[0]["IPAM"]["Config"],
            "nginxTestExit": 0, "imageId": audited_image,
        })
        self.dist("test", "-s", f"{ROOT}/authority/HEAD")
        self.dist("grep", "-Eq", '"generation"[[:space:]]*:[[:space:]]*0', f"{ROOT}/authority/HEAD")
        logs = self.compose("logs", "--no-color", "--no-log-prefix", "dist")
        if "Initializing empty /dt/store" not in logs:
            raise RuntimeError("Empty root was not observed at first start")
        self.unknown_root()

    def unknown_root(self):
        volume = self.project + "_unknown"
        label = f"uedt.run={self.run_id}"
        run("docker", "volume", "create", "--label", label, volume)
        script = r"""
set -euo pipefail
root=/dt/store
mkdir -p "$root"/{incoming,keys,publish,ue-dt-updates/catalogs}
printf 'legacy fixture\n' >"$root/ue-dt-updates/catalogs/legacy.json"
chown uedt-distribution:uedt-upload "$root"
chmod 0711 "$root"
inventory() {
    (cd "$root"; find . -printf '%y %m %u:%g %T@ %P\n' | LC_ALL=C sort
     find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum)
}
before=$(inventory)
code=0
/dt/tools/entrypoint.sh admin list --error-details >/tmp/legacy.log 2>&1 || code=$?
[[ $code == 1 ]]
grep -q 'AuthorityRootException' /tmp/legacy.log
code=0
/dt/tools/entrypoint.sh admin promotion migrate --dry-run >/tmp/migrate.log 2>&1 || code=$?
[[ $code == 1 ]]
grep -F 'UnrecognizedLayout:' /tmp/migrate.log
[[ $(inventory) == "$before" ]]
printf '%s\n' "$before"
"""
        try:
            evidence = run("docker", "run", "--rm", "-i", "--network", "none", "--label", label,
                           "--mount", f"type=volume,source={volume},target={ROOT},volume-nocopy",
                           "--mount", f"type=volume,source={self.project}_config,target=/dt/config,readonly",
                           "--entrypoint", "bash", self.image, "-s", input_text=script)
            (self.work / "unknown-root.txt").write_text(evidence + "\n", encoding="utf-8")
        finally:
            run("docker", "volume", "rm", volume)

    def down(self):
        label = f"label=uedt.run={self.run_id}"
        for container in run("docker", "ps", "-aq", "--filter", label).splitlines():
            name = run("docker", "inspect", "--format", "{{.Name}}", container).lstrip("/")
            if name in {self.linux, self.simulator}:
                run("docker", "rm", "-f", container)
        self.compose("--profile", "setup", "down", "-v", timeout=120)
        for image in set(run("docker", "image", "ls", "-aq", "--no-trunc", "--filter", label).splitlines()):
            run("docker", "image", "rm", image)
        residual = {category: run("docker", *args, "--filter", label).splitlines() for category, args in (
            ("container", ("ps", "-aq")), ("network", ("network", "ls", "-q")),
            ("volume", ("volume", "ls", "-q")), ("image", ("image", "ls", "-aq")))}
        self.save("docker-residual.json", residual)
        if any(residual.values()):
            raise RuntimeError("Run-labelled Docker resources remain")

    def direct(self):
        network = self.project + "_default"
        dist = self.compose("ps", "-q", "dist")
        ip = self.container_address(dist, network)
        label = f"uedt.run={self.run_id}"
        run("docker", "build", "--platform", "linux/amd64", "--build-arg", f"RUN_ID={self.run_id}",
            "-t", self.sim_image, self.work / "sim-context", timeout=900)
        run("docker", "run", "-d", "--init", "--name", self.simulator, "--label", label, "--network", network,
            "--add-host", f"host.docker.internal:{ip}", self.sim_image, "sleep", "infinity")
        try:
            peer = self.container_address(self.simulator, network)
            registration = self.work / "simulator-public.json"
            run("docker", "exec", self.simulator, "python", "/driver.py", "keygen", "--key-id", "prod-direct",
                "--work-dir", "/tmp/identity", sensitive=True)
            run("docker", "cp", f"{self.simulator}:/tmp/identity/device-public.json", registration)
            run("docker", "cp", self.work / "windows-client" / "release-public.pem", f"{self.simulator}:/tmp/public.pem")
            self.register("simulator", peer, registration)
            for name, extra in (("get", ()), ("range", ("--offset", "1"))):
                report = f"/tmp/{name}.json"
                run("docker", "exec", self.simulator, "python", "/driver.py", "download", "--origin", ORIGIN,
                    "--key-id", "prod-direct", "--project", PROJECT, "--platform", "linux-x64", "--version", VERSION,
                    "--path", "version.txt", "--work-dir", "/tmp/identity", "--signing-public-key", "/tmp/public.pem",
                    "--signing-key-id", "prod-01d-release", "--report", report, *extra)
                run("docker", "cp", f"{self.simulator}:{report}", self.work / f"direct-{name}.json")
            get = json.loads((self.work / "direct-get.json").read_text(encoding="utf-8"))
            ranged = json.loads((self.work / "direct-range.json").read_text(encoding="utf-8"))
            full, = (row for row in get["requests"] if row["kind"] == "file")
            partial, = (row for row in ranged["requests"] if row["kind"] == "file-range")
            size = full["bytes"]
            expected_range = {"start": 1, "end": size - 1, "total": size}
            if full["status"] != 200 or partial["status"] != 206 or partial.get("contentRange") != expected_range:
                raise RuntimeError("Signed GET or Range did not produce the expected 200/206 and Content-Range")
            access = self.compose("logs", "--no-color", "--no-log-prefix", "nginx")
            rows = [line.split() for line in access.splitlines() if "/files/version.txt " in line]
            matching = [row for row in rows if len(row) >= 4 and row[0] == peer and row[2] == "18080"]
            if {row[1] for row in matching} != {"200", "206"}:
                raise RuntimeError(f"nginx did not distinguish the direct simulator IP {peer}")
            self.save("direct-proof.json", {"origin": ORIGIN, "dist": ip, "simulator": peer, "logs": matching,
                                            "getStatus": 200, "rangeStatus": 206, "contentRange": expected_range})
        finally:
            run("docker", "rm", "-f", self.simulator)

    @staticmethod
    def container_address(container, network):
        entry = json.loads(run("docker", "inspect", container))[0]
        return str(ipaddress.IPv4Address(entry["NetworkSettings"]["Networks"][network]["IPAddress"]))

    def save(self, name, value):
        (self.work / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def copy_to(self, service, source, destination):
        self.compose("cp", source, f"{service}:{destination}")

    def ready(self):
        for _ in range(60):
            info = self.compose("ps", "--all", "--format", "json")
            services = json.loads(info) if info.startswith("[") else [json.loads(line) for line in info.splitlines()]
            if any(item["State"] in {"dead", "exited"} for item in services):
                raise RuntimeError("Compose service exited during startup")
            healthy = {item["Service"] for item in services if item.get("Health") == "healthy"}
            running = {item["Service"] for item in services if item["State"] == "running"}
            if running == {"dist", "nginx", "sshd"} and healthy == {"dist", "nginx"}:
                return services
            time.sleep(2)
        raise TimeoutError("Compose services did not become healthy")

    def upload(self, launcher, payload, project, version, platform, entry):
        sidecar = payload.with_name("release.json")
        run(launcher, "release-metadata", "--zip", payload, "--project-id", project,
            "--version", version, "--platform", platform, "--entry-point", entry,
            "--executable-paths", entry, "--output", sidecar)
        incoming = f"{ROOT}/incoming/{payload.parent.name}"
        self.dist("mkdir", incoming)
        self.dist("touch", f"{incoming}/transfer.uploading")
        self.copy_to("dist", payload, f"{incoming}/payload.zip")
        self.copy_to("dist", sidecar, f"{incoming}/release.json")
        self.dist("chown", "-R", "uedt-distribution:uedt-upload", incoming)
        return incoming

    def intake(self, incoming):
        self.dist("rm", f"{incoming}/transfer.uploading")
        try:
            job = json.loads(self.server("ingest", incoming, timeout=900))
        except RuntimeError:
            # The running product scanner can claim this path before the manual command.
            job = None
            for _ in range(60):
                jobs = json.loads(self.server("list"))
                job = next((j for j in jobs if j["source"] == incoming and j["state"] in {"pending", "failed"}), None)
                if job:
                    break
                time.sleep(1)
            if not job:
                raise
        if job["state"] != "pending":
            raise RuntimeError(f"Expected pending intake, got {job['state']}: {job.get('message')}")
        return job["id"]

    def verify_uploader(self):
        container = self.compose("ps", "-q", "sshd")
        instance = json.loads(run("docker", "inspect", container))[0]
        mounts = {entry["Destination"]: entry for entry in instance["Mounts"]}
        expected = {f"{ROOT}/incoming": True, f"{ROOT}/upload-status": False}
        if {path: item["RW"] for path, item in mounts.items()} != expected:
            raise RuntimeError("Unexpected sshd mount paths or modes")
        if {item["Name"] for item in mounts.values()} != {self.project + "_store"}:
            raise RuntimeError("sshd did not share only store subpaths")
        subpaths = {entry["Target"]: entry.get("VolumeOptions", {}).get("Subpath")
                    for entry in instance["HostConfig"]["Mounts"]}
        if subpaths != {path: path.rsplit("/", 1)[1] for path in expected}:
            raise RuntimeError("sshd volume subpaths differ from the expected directories")
        statuses = ""
        for _ in range(15):
            statuses = self.dist("find", f"{ROOT}/upload-status", "-maxdepth", "1", "-name", "*.json",
                                 "-type", "f", "-printf", "%m %u:%g %f\\n")
            if statuses:
                break
            time.sleep(1)
        if not statuses or any(not line.startswith("640 uedt-distribution:uedt-upload ") for line in statuses.splitlines()):
            raise RuntimeError(f"Status file ownership or mode differs: {statuses}")
        probe = (
            'set -eu; test "$(id -un)" = uploader; '
            'for p in /dt/store/authority /dt/store/releases /dt/store/processing /dt/config /dt/keys; do '
            'if ls -A "$p" >/dev/null 2>&1; then exit 1; fi; done; '
            'status=$(find /dt/store/upload-status -maxdepth 1 -type f -name "*.json" -print -quit); '
            'test -n "$status"; cat "$status" >/dev/null; '
            'if touch /dt/store/upload-status/.permission-probe 2>/dev/null; then exit 1; fi; '
            'probe=$(mktemp /dt/store/incoming/.permission-probe-XXXXXX); '
            'printf success > "$probe"; test "$(cat "$probe")" = success; rm -- "$probe"'
        )
        self.compose("exec", "-T", "sshd", "setpriv", "--reuid=uploader", "--regid=uedt-upload",
                     "--clear-groups", "/bin/bash", "-c", probe)
        permissions = self.dist("stat", "-c", "%a %U:%G %n", ROOT, f"{ROOT}/incoming", f"{ROOT}/upload-status",
                                f"{ROOT}/processing", f"{ROOT}/releases", f"{ROOT}/authority",
                                "/dt/config", f"{KEYS}/signing.pem")
        mode_expected = ["711 uedt-distribution:uedt-upload", "2770 uedt-distribution:uedt-upload",
                         "750 uedt-distribution:uedt-upload", "700 uedt-distribution:uedt-upload",
                         "700 uedt-distribution:uedt-upload", "700 uedt-distribution:uedt-upload",
                         "750 root:uedt-distribution", "400 uedt-distribution:uedt-distribution"]
        if [line.rsplit(" ", 1)[0] for line in permissions.splitlines()] != mode_expected:
            raise RuntimeError(f"Unexpected store/config/keys permissions: {permissions}")
        self.save("uploader-isolation.json", {
            "uid": self.compose("exec", "-T", "sshd", "id", "uploader"),
            "mounts": [{"volume": v["Name"], "path": v["Destination"], "writable": v["RW"],
                        "subpath": subpaths[v["Destination"]]} for v in mounts.values()],
            "probeExit": 0, "permissions": permissions.splitlines(), "statusModes": statuses.splitlines(),
        })

    def measure(self, launcher, source):
        copies = self.work / "ma0t10-copy"
        copies.mkdir()
        original = source.absolute()
        if not original.is_dir() or copies.resolve().is_relative_to(original.resolve()):
            raise ValueError("Expected the original package directory outside fixture work")
        if any(path.is_symlink() or path.is_junction() for path in (original, *original.parents)):
            raise ValueError("Package root contains a filesystem link")
        source_files = inventory(original)
        if "ma0t10_dt.exe" not in source_files:
            raise ValueError("Expected the ma0t10_dt Windows package")
        for name in source_files:
            destination = copies / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(original / name, destination)
        if inventory(copies) != source_files or inventory(original) != source_files:
            raise ValueError("Package copy did not match the original inventory")
        upload = self.work / "ma0t10-measure"
        upload.mkdir()
        payload = upload / "payload.zip"
        with zipfile.ZipFile(payload, "x", zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
            for name in source_files:
                archive.write(copies / name, name)
        with zipfile.ZipFile(payload) as archive:
            for item in archive.infolist():
                with archive.open(item) as file:
                    if (item.file_size, hashlib.file_digest(file, "sha256").hexdigest()) != source_files[item.filename]:
                        raise ValueError("ZIP member differs from the copied payload")
        if inventory(original) != source_files:
            raise ValueError("Source package changed during ZIP preparation")
        self.save("ma0t10-source-inventory.json", source_files)

        before = self.sample("before")[0]
        incoming = self.upload(launcher, payload, "ma0t10-dt", "1.0.0", "windows-x64", "ma0t10_dt.exe")
        uploaded = self.sample("uploaded")[0]
        job = self.intake(incoming)
        pending = self.sample("pending")[0]
        approving = self.sample("approving", job)
        if not approving:
            raise RuntimeError("No valid du sample collected while approving")
        published = self.sample("published")[0]
        if json.loads(self.server("inspect", job))["state"] != "published":
            raise RuntimeError("Measured release did not reach published state")
        self.promote("ma0t10-dt", "windows-x64")
        promoted = self.sample("promoted")[0]
        size = sum(length for length, _ in source_files.values())
        stages = {"before": before, "uploaded": uploaded, "pending": pending,
                  "approvingSamples": approving, "published": published, "promoted": promoted}
        baseline = sum(before.values())
        steady = sum(published.values()) - baseline
        peak = max(sum(sample.values()) for sample in (uploaded, pending, *approving, published, promoted)) - baseline
        self.save("storage-measurement.json", {"payloadBytes": size, "payloadFiles": len(source_files),
                  "zipBytes": payload.stat().st_size, "job": job, "sampleIntervalMs": 50,
                  "phases": stages, "steadyBytes": steady, "sampledPeakBytes": peak,
                  "k": steady / size, "sampledPeakMultiplier": peak / size})
        if steady / size > 3.57:
            raise RuntimeError(f"k={steady / size:.4f} exceeds the 3.57 decision threshold")

    def sample(self, phase, *args):
        sampler = (TEMPLATES / "measure.sh").read_text(encoding="utf-8")
        output = self.dist("bash", "-s", "--", phase, *args, input_text=sampler, timeout=900)
        return [{name: int(size) for _, name, size in (line.split() for line in block.splitlines()
                                                       if line.startswith(phase + " "))}
                for block in output.split("\n\n") if block.startswith(phase + " ")]

    def restart_proof(self):
        command = 'cd "$1" && find authority releases -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum'
        before = self.dist("bash", "-e", "-o", "pipefail", "-c", command, "_", ROOT)
        (self.work / "restart-before.sha256").write_text(before + "\n", encoding="utf-8")
        # The sidecars share dist's network namespace. Stop them before that
        # namespace goes away and rejoin after the new backend is healthy.
        self.compose("stop", "nginx", "sshd")
        self.compose("restart", "--no-deps", "dist")
        container = self.compose("ps", "--all", "-q", "dist")
        for _ in range(45):
            state = json.loads(run("docker", "inspect", "--format", "{{json .State}}", container))
            if state["Health"]["Status"] == "healthy":
                break
            if not state["Running"]:
                raise RuntimeError("dist stopped during ordered restart")
            time.sleep(1)
        else:
            raise TimeoutError("dist did not become healthy after restart")
        self.compose("restart", "--no-deps", "nginx", "sshd")
        self.ready()
        after = self.dist("bash", "-e", "-o", "pipefail", "-c", command, "_", ROOT)
        (self.work / "restart-after.sha256").write_text(after + "\n", encoding="utf-8")
        if before != after:
            raise RuntimeError("Authority or release inventory changed across restart")
        self.save("restart-proof.json", {"authorityFiles": sum("  authority/" in line for line in before.splitlines()),
                                         "releaseFiles": sum("  releases/" in line for line in before.splitlines()),
                                         "identical": True})

    def publish_demo(self, launcher, platform, windows_entry):
        entry = "game.exe" if platform == "windows-x64" else "game.sh"
        upload = self.work / platform
        upload.mkdir()
        payload = upload / "payload.zip"
        with zipfile.ZipFile(payload, "x", zipfile.ZIP_DEFLATED) as files:
            if platform == "windows-x64":
                files.write(windows_entry, entry)
            else:
                files.writestr(entry, "#!/bin/sh\nexit 0\n")
            files.writestr("version.txt", f"Isolated test release {VERSION}\n")
        incoming = self.upload(launcher, payload, PROJECT, VERSION, platform, entry)
        job = self.intake(incoming)
        self.server("approve", job)
        approval = json.loads(self.server("inspect", job))
        if approval["state"] != "published":
            raise RuntimeError("Synthetic release approval did not reach published state")
        promotion = self.promote(PROJECT, platform)
        self.save(f"{platform}-publication.json", {"job": job, "approval": approval, "promotion": promotion})

    def promote(self, project, platform):
        track = ("--project-id", project, "--environment", "prod", "--channel", "stable", "--platform", platform)
        revision = json.loads(self.server("promotion", "inspect", *track))["revision"]
        return json.loads(self.server("promote", *track, "--version", VERSION,
                                      "--expected-revision", revision, "--reason", "isolated PROD-01D fixture"))

    def nginx_peer(self):
        logs = self.compose("logs", "--no-color", "--no-log-prefix", "--tail", "40", "nginx")
        for line in reversed(logs.splitlines()):
            fields = line.split()
            if len(fields) >= 4 and fields[1:4] == ["401", "80", "/api/v1/catalog"]:
                address = ipaddress.ip_address(fields[0])
                if not address.is_loopback:
                    return str(address)
        raise RuntimeError("No nginx peer for the unauthenticated catalog probe")

    def register(self, client, address, public_key):
        policy = self.work / "access-policy.json"
        clients = json.loads(policy.read_text(encoding="utf-8"))["clients"] if policy.exists() else []
        clients.append({
            "id": client, "addresses": [address],
            "grants": [{"projectId": PROJECT, "environment": "prod", "channel": "stable", "versions": [VERSION]}],
        })
        self.save("access-policy.json", {"clients": clients})
        self.compose("run", "--rm", "--no-deps", "-T", "setup", "policy", input_text=policy.read_text(encoding="utf-8"))
        destination = f"{ENTRY_HOME}/{client}-public.json"
        self.copy_to("dist", public_key, destination)
        self.dist("chmod", "0644", destination)
        self.server("client-key", "add", "--client", client, "--public-key", destination)

    def client_config(self, command, platform, credential, public_key, config):
        command("sample-config", "--deployment-mode", "portable", "--auth", "request-signature-v1",
                "--server-url", ORIGIN, "--project-id", PROJECT, "--platform", platform,
                "--credential-name", credential, "--signing-key-id", "prod-01d-release",
                "--public-key", public_key, "--output", config)

    def verify_install(self, directory, platform):
        selection = Path(PROJECT, "prod", "stable", VERSION, platform)
        state = directory / ".state" / selection / "installed-manifest.json"
        manifest = json.loads(state.read_text(encoding="utf-8-sig"))
        if manifest["version"] != VERSION or manifest["platform"] != platform or not manifest["files"]:
            raise RuntimeError("Installed manifest selection differs from the published demo")
        for item in manifest["files"]:
            payload = (directory / "app" / selection / item["path"]).resolve()
            if not payload.is_relative_to((directory / "app" / selection).resolve()):
                raise ValueError("Manifest file is outside the test installation")
            if payload.stat().st_size != item["size"] or digest(payload) != item["sha256"].lower():
                raise RuntimeError(f"Installed hash or size mismatch: {item['path']}")
        return {"origin": ORIGIN, "exit": 0, "platform": platform, "matchingFiles": len(manifest["files"]),
                "manifestSha256": digest(state)}

    def connectivity(self, win_cohort, linux_host_gateway=False):
        if win_cohort["rid"] != "win-x64":
            raise ValueError("Use a win-x64 publication")
        launcher = published(win_cohort, "launcher")
        windows_entry = Path(win_cohort["fixtureAssets"]["synthetic"]["path"])
        if digest(windows_entry) != win_cohort["fixtureAssets"]["synthetic"]["sha256"]:
            raise ValueError("Windows demo hash differs from publication")
        code = run("curl.exe", "--noproxy", "*", "--max-time", "15", "-sS", "-o", "NUL",
                   "-w", "%{http_code}", ORIGIN + "/api/v1/catalog")
        self.save("windows-anonymous.json", {"origin": ORIGIN, "httpStatus": int(code)})
        if code != "401":
            raise RuntimeError(f"Anonymous Windows catalog expected 401, got {code}")
        address = self.nginx_peer()
        self.publish_demo(launcher, "windows-x64", windows_entry)
        windows = self.work / "windows-client"
        windows.mkdir()
        public = windows / "release-public.pem"
        self.compose("cp", f"dist:{KEYS}/release-public.pem", public)
        os.environ["UE_DT_AGENT_DATA_ROOT"] = str(windows / "private")
        credential = f"prod-{self.run_id}-win"
        device = windows / "device-public.json"
        run(launcher, "credential", "keygen", "--name", credential, "--key-id", credential,
            "--public-out", device, "--storage", "portable", sensitive=True)
        try:
            self.register("windows", address, device)
            config = windows / "launcher.json"
            self.client_config(lambda *args: run(launcher, *args), "windows-x64", credential, public, config)
            output = run(launcher, "run", "--no-launch", "--config", config)
            (self.work / "windows-install.log").write_text(output + "\n", encoding="utf-8")
            self.save("windows-install.json", self.verify_install(windows, "windows-x64") | {"peer": address})
        finally:
            run(launcher, "credential", "delete", "--name", credential, "--storage", "portable", sensitive=True)

        self.publish_demo(launcher, "linux-x64", windows_entry)
        self.connect_linux(linux_host_gateway)

    def connect_linux(self, linux_host_gateway=False):
        public = self.work / "windows-client" / "release-public.pem"
        if not public.is_file() or not (self.work / "linux-x64-publication.json").is_file():
            raise RuntimeError("Prepare and promote the synthetic Linux release first")

        def start(alias=None):
            owned = run("docker", "ps", "-aq", "--filter", f"label=uedt.run={self.run_id}",
                        "--filter", f"name=^/{self.linux}$")
            if owned:
                run("docker", "rm", "-f", owned)
            hosts = ["--add-host", f"host.docker.internal:{alias}"] if alias else []
            run("docker", "run", "-d", "--init", "--name", self.linux, "--label", f"uedt.run={self.run_id}",
                "--network", self.project + "_default", *hosts, "--entrypoint", "/usr/bin/sleep", self.image, "infinity")

        alias = "host-gateway" if linux_host_gateway else None
        start(alias)
        try:
            lookup = self.client("getent", "ahostsv4", "host.docker.internal")
        except RuntimeError:
            alias = "host-gateway"
            start(alias)
            lookup = self.client("getent", "ahostsv4", "host.docker.internal")
        probe = ("curl", "--noproxy", "*", "--max-time", "5", "-sS", "-o", "/dev/null", "-w", "%{http_code}")
        url = ORIGIN + "/api/v1/catalog"
        first_lookup = lookup
        try:
            code = self.client(*probe, url)
        except RuntimeError as first_error:
            # Desktop can resolve its host alias to a different interface from the
            # approved LAN-only port binding. Verify the LAN path under the same origin.
            code = self.client(*probe, "--resolve", "host.docker.internal:18080:192.168.0.2", url)
            if code != "401":
                raise first_error
            alias = "192.168.0.2"
            start(alias)
            lookup = self.client("getent", "ahostsv4", "host.docker.internal")
            code = self.client(*probe, url)
        self.save("linux-dns.json", {"origin": ORIGIN, "lookup": lookup.splitlines(),
                                     "initialLookup": first_lookup.splitlines(), "hostAlias": alias})
        if code != "401":
            raise RuntimeError(f"Anonymous Linux catalog expected 401, got {code}")
        address = self.nginx_peer()
        linux = self.work / "linux-client"
        linux.mkdir()
        temp = "/tmp/prod-client"
        self.client("install", "-d", "-m", "0700", temp)
        run("docker", "cp", public, f"{self.linux}:{temp}/release-public.pem")
        credential = f"prod-{self.run_id}-linux"
        self.client(LINUX_CLI, "credential", "keygen", "--name", credential, "--key-id", credential,
                    "--public-out", f"{temp}/device-public.json", "--storage", "portable", sensitive=True)
        device = linux / "device-public.json"
        run("docker", "cp", f"{self.linux}:{temp}/device-public.json", device)
        self.register("linux", address, device)
        config = f"{temp}/launcher.json"
        self.client_config(lambda *args: self.client(LINUX_CLI, *args), "linux-x64", credential,
                           f"{temp}/release-public.pem", config)
        output = self.client(LINUX_CLI, "run", "--no-launch", "--config", config)
        (self.work / "linux-install.log").write_text(output + "\n", encoding="utf-8")
        run("docker", "cp", f"{self.linux}:{temp}/.", linux)
        self.save("linux-install.json", self.verify_install(linux, "linux-x64") | {"peer": address})
        self.verify_uploader()


def inventory(root):
    result = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink() or path.is_junction():
            raise ValueError("Package contains a filesystem link")
        if path.is_file():
            result[path.relative_to(root).as_posix()] = (path.stat().st_size, digest(path))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["stage", "up", "connect", "connect-linux", "direct", "measure", "restart", "down"])
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--linux-cohort", type=Path)
    parser.add_argument("--server-multi", type=Path)
    parser.add_argument("--ssh-public-key", type=Path)
    parser.add_argument("--win-cohort", type=Path)
    parser.add_argument("--linux-host-gateway", action="store_true")
    parser.add_argument("--ue-package", type=Path)
    args = parser.parse_args()
    fixture = Fixture(args)
    if args.action == "stage":
        if not args.linux_cohort:
            parser.error("stage requires --linux-cohort")
        fixture.stage(json.loads(args.linux_cohort.read_text(encoding="utf-8")), args.ssh_public_key)
        print(fixture.context)
    elif args.action == "up":
        if not args.server_multi:
            parser.error("up requires --server-multi")
        fixture.up(args.server_multi.resolve(strict=True))
    elif args.action == "direct":
        fixture.direct()
    elif args.action == "connect-linux":
        fixture.connect_linux(args.linux_host_gateway)
    elif args.action == "connect":
        if not args.win_cohort:
            parser.error("connect requires --win-cohort")
        fixture.connectivity(json.loads(args.win_cohort.read_text(encoding="utf-8")), args.linux_host_gateway)
    elif args.action == "measure":
        if not args.win_cohort or not args.ue_package:
            parser.error("measure requires --win-cohort and --ue-package")
        cohort = json.loads(args.win_cohort.read_text(encoding="utf-8"))
        fixture.measure(published(cohort, "launcher"), args.ue_package)
    elif args.action == "restart":
        fixture.restart_proof()
    else:
        fixture.down()


if __name__ == "__main__":
    main()
