"""Run the 01S7 Linux filesystem matrix in disposable Docker Desktop resources."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import uuid


HERE = Path(__file__).resolve().parent
DOCKER_PIPE = "npipe:////./pipe/dockerDesktopLinuxEngine"
DOCKER = ("docker", "--host", DOCKER_PIPE)
HARNESS = "/opt/checks/harness/UeDtLauncher.AuthorityHarness"
HARNESS_DLL = HARNESS + ".dll"
ENTRYPOINT = "/opt/checks/rocky-platform.sh"
RESTART_CASES = ("GenerationRename", "HeadRename", "AuditAppend")


class Run:
    def __init__(self, publish, output, final):
        self.publish = publish.resolve(strict=True)
        self.output = output.resolve()
        self.output.mkdir(parents=True)
        self.final = final
        self.id = uuid.uuid4().hex
        self.project = f"uedt-prod-{self.id}"
        self.image = f"uedt-authority-platform:{self.id}"
        self.label = f"uedt.run={self.id}"
        self.compose = self.output / "compose.json"
        self.results = {}
        self.docker_env = os.environ.copy()
        for name in ("DOCKER_CONTEXT", "BUILDX_BUILDER", "BUILDKIT_HOST"):
            self.docker_env.pop(name, None)
        self.docker_env["DOCKER_HOST"] = DOCKER_PIPE

    def log(self, name, message):
        with (self.output / f"{name}.log").open("a", encoding="utf-8") as sink:
            sink.write(message + "\n")

    def command(self, log, *args, expected=0, timeout=300, env=None):
        self.log(log, f"$ {subprocess.list2cmdline(args)}")
        result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding="utf-8", errors="replace", timeout=timeout, env=env)
        self.log(log, result.stdout + f"[exit {result.returncode}]")
        if result.returncode != expected:
            raise RuntimeError(f"{args[:3]} returned {result.returncode}, expected {expected}; see {log}.log")
        return result.stdout

    def docker(self, log, *args, **kwargs):
        return self.command(log, *DOCKER, *args, env=self.docker_env, **kwargs)

    def dc(self, log, *args, **kwargs):
        return self.command(log, *self.compose_args(*args), env=self.docker_env, **kwargs)

    def compose_args(self, *args):
        return (*DOCKER, "compose", "-p", self.project, "-f", str(self.compose), *args)

    def collect(self, phase):
        state = {}
        checks = {
            "containers": ("ps", "-a", "--no-trunc", "--format", "{{.ID}} {{.Names}}"),
            "networks": ("network", "ls", "--no-trunc", "--format", "{{.ID}} {{.Name}}"),
            "volumes": ("volume", "ls", "--format", "{{.Name}}"),
            "images": ("image", "ls", "-a", "--no-trunc", "--digests", "--format",
                       "{{.ID}} {{.Repository}}:{{.Tag}} {{.Digest}}"),
        }
        for name, args in checks.items():
            state[name] = sorted(self.docker(f"{phase}-resources", *args).splitlines())
        self.docker(f"{phase}-resources", "system", "df")
        for name, args in {
            "loops": ("losetup", "-a"),
            "loop-devices": ("sh", "-c", "ls -d /sys/block/loop*"),
        }.items():
            values = self.command(f"{phase}-resources", "wsl", "-d", "docker-desktop", "--", *args)
            state[name] = sorted(values.splitlines())
        (self.output / f"{phase}-resources.json").write_text(json.dumps(state, indent=2), encoding="utf-8")
        return state

    def prepare(self):
        with tempfile.TemporaryDirectory(prefix="build-", dir=self.output) as folder:
            folder = Path(folder)
            shutil.copytree(self.publish, folder / "harness")
            with (folder / "harness" / Path(HARNESS_DLL).name).open("rb") as binary:
                self.harness_digest = hashlib.file_digest(binary, "sha256").hexdigest()
            self.log("setup", f"Staged Linux harness DLL SHA-256 {self.harness_digest}")
            self.results["harnessDllSha256"] = self.harness_digest
            for source, name in (("rocky-platform.sh", "rocky-platform.sh"),
                                 ("rocky-platform.Dockerfile", "Dockerfile")):
                content = (HERE / source).read_bytes().replace(b"\r\n", b"\n")
                (folder / name).write_bytes(content)
            self.docker("setup", "build", "--builder", "default", "--platform", "linux/amd64", "--progress=plain",
                        "--build-arg", f"RUN_ID={self.id}", "-t", self.image, str(folder), timeout=720)
        self.docker("setup", "image", "inspect", self.image,
                    "--format", "{{.Id}} {{.Os}}/{{.Architecture}} {{json .Config.Labels}}")

    def configure(self):
        def service(fs, volume):
            return {
                "image": self.image, "pull_policy": "never",
                "labels": {"uedt.run": self.id}, "network_mode": "none",
                "environment": {"UEDT_RUN": self.id},
                "command": [ENTRYPOINT, fs],
                "volumes": [volume], "tmpfs": ["/unsupported"],
            }
        ext4 = service("ext4", "ext4:/store")
        xfs = service("xfs", "disk:/disk")
        xfs["privileged"] = True
        definition = {"services": {"ext4": ext4, "xfs": xfs}, "volumes": {
            name: {"labels": {"uedt.run": self.id}} for name in ("ext4", "disk")
        }}
        self.compose.write_text(json.dumps(definition, indent=2), encoding="utf-8")

    def inside(self, fs, log, *args, **kwargs):
        return self.dc(log, "exec", "-T", fs, *args, **kwargs)

    def mount(self, fs, root, log):
        deadline = time.monotonic() + 25
        while True:
            try:
                options = self.inside(fs, log, "findmnt", "-n", "-o", "TARGET,SOURCE,FSTYPE,OPTIONS",
                                      "--mountpoint", root).strip()
                if len(options.split()) < 4 or options.split()[2] != fs:
                    raise RuntimeError(f"{root}: expected {fs}; got {options}")
                return options
            except RuntimeError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(1)

    def suite(self, fs, root):
        profile = ("final",) if self.final else ()
        self.inside(fs, f"{fs}-suite", HARNESS, "platform-suite", f"{root}/suite", *profile, timeout=900)

    def restart(self, fs, root, boundary, index, container):
        log = f"{fs}-restart-{index}"
        target = f"{root}/restart-{index}"
        self.inside(fs, log, HARNESS, "platform-seed", target)
        args = self.compose_args("exec", "-T", fs, HARNESS, "crash", target, boundary, "mixed")
        self.log(log, f"$ {subprocess.list2cmdline(args)}")
        # A separate Docker exec has the writer's stdout. Poll its file with a deadline
        # so the restart is issued only after the existing 01S1 seam is reached.
        writer_log = self.output / f"{log}-writer.log"
        with writer_log.open("wb") as sink:
            writer = subprocess.Popen(args, stdout=sink, stderr=subprocess.STDOUT, env=self.docker_env)
            try:
                deadline = time.monotonic() + 45
                while b"BOUNDARY" not in writer_log.read_bytes().splitlines():
                    if writer.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError(f"{fs} writer did not reach {boundary}; see {writer_log.name}")
                    time.sleep(0.1)
                before = self.docker(log, "inspect", "--format", "{{.State.StartedAt}}", container).strip()
                self.docker(log, "restart", "--timeout", "0", container)
                exit_code = writer.wait(timeout=30)
                self.log(log, f"Writer reached {boundary}; interrupted Docker exec exit {exit_code}")
                if exit_code == 0:
                    raise RuntimeError("Interrupted writer returned success.")
                after = self.docker(log, "inspect", "--format", "{{.State.StartedAt}}", container).strip()
                if before == after:
                    raise RuntimeError("Container start timestamp did not change.")
                self.mount(fs, root, log)
                self.inside(fs, log, HARNESS, "platform-verify", target, boundary)
            finally:
                # Close only the owned CLI child. The container may need to be
                # preserved if the XFS mount cannot be safely detached.
                if writer.poll() is None:
                    writer.kill()
                    writer.wait(timeout=10)

    def run_fs(self, fs):
        log = f"{fs}-mount"
        root = "/store" if fs == "ext4" else "/xfs"
        result = {"mount": "NOT_RUN", "suite": "NOT_RUN", "restart": [], "filesystem": "NOT_RUN"}
        self.results[fs] = result
        try:
            self.dc(log, "up", "-d", "--no-deps", fs)
            container = self.dc(log, "ps", "-q", fs).strip()
            if not container:
                raise RuntimeError("No running test container.")
            self.docker(log, "inspect", container, "--format",
                        "{{.Id}} {{.Image}} {{.HostConfig.Privileged}} {{json .NetworkSettings.Ports}} {{json .Mounts}}")
            result["mount"] = self.mount(fs, root, log)
            digest = self.inside(fs, log, "sha256sum", HARNESS_DLL).split()
            if not digest or digest[0] != self.harness_digest:
                raise RuntimeError(f"{fs}: harness DLL differs from the staged test input.")
            result["harnessDllSha256"] = digest[0]
            self.inside(fs, log, "cat", "/etc/redhat-release")
            self.inside(fs, log, "uname", "-r")
            self.inside(fs, log, "stat", "-f", "-c", "%t %T", root)
            self.inside(fs, log, "rpm", "-q", "libicu", "openssl-libs", "util-linux", "xfsprogs")
            try:
                self.suite(fs, root)
                result["suite"] = "PASS"
            except (RuntimeError, subprocess.TimeoutExpired) as error:
                result["suite"] = "FAIL"
                self.log(log, str(error))
            if not self.final:
                for index, boundary in enumerate(RESTART_CASES, 1):
                    try:
                        self.restart(fs, root, boundary, index, container)
                        status = "PASS"
                    except (RuntimeError, subprocess.TimeoutExpired) as error:
                        status = "FAIL"
                        self.log(log, str(error))
                    result["restart"].append({"seam": boundary, "status": status})
            try:
                probe = f"{root}/filesystem-probe"
                self.inside(fs, log, "mkdir", "--", probe)
                self.inside(fs, log, HARNESS, "probe", probe)
                self.inside(fs, log, "findmnt", "-n", "-o", "FSTYPE,OPTIONS", "--mountpoint", "/unsupported")
                refused = self.inside(fs, log, HARNESS, "probe", "/unsupported", expected=3)
                if "UnsupportedFileSystem:" not in refused or "0x01021994" not in refused:
                    raise RuntimeError(f"Expected a statfs tmpfs refusal; got {refused.strip()}.")
                remaining = self.inside(fs, log, "find", "/unsupported", "-mindepth", "1", "-print", "-quit")
                if remaining.strip():
                    raise RuntimeError("The unsupported tmpfs changed after refusal.")
                result["filesystem"] = "PASS"
            except (RuntimeError, subprocess.TimeoutExpired) as error:
                result["filesystem"] = "FAIL"
                self.log(log, str(error))
        except (RuntimeError, subprocess.TimeoutExpired) as error:
            self.log(log, str(error))
        finally:
            try:
                self.dc(log, "logs", "--no-color", fs)
            except RuntimeError as error:
                self.log(log, str(error))

    def cleanup_xfs(self):
        if self.dc("cleanup", "ps", "-q", "xfs").strip():
            self.inside("xfs", "cleanup", ENTRYPOINT, "cleanup")
            return
        volumes = self.docker("cleanup", "volume", "ls", "-q", "--filter", f"label={self.label}").splitlines()
        if f"{self.project}_disk" in volumes:
            self.dc("cleanup", "run", "--rm", "--no-deps", "-T", "xfs", ENTRYPOINT, "cleanup")

    def run(self):
        engine = self.docker("setup", "info", "--format", "{{.OSType}}|{{.OperatingSystem}}|{{.ID}}|{{.KernelVersion}}")
        os_type, operating_system, engine_id, kernel = engine.strip().split("|")
        if os_type != "linux" or not operating_system.startswith("Docker Desktop") or not engine_id or "WSL2" not in kernel:
            raise RuntimeError(f"Expected Docker Desktop Linux on the local pipe {DOCKER_PIPE}; got {engine.strip()}.")
        self.results["engine"] = {"host": DOCKER_PIPE, "id": engine_id, "kernel": kernel}
        before = self.collect("before")
        xfs_attempted = False
        configured = False
        cleanup_errors = []
        try:
            self.prepare()
            self.configure()
            configured = True
            self.run_fs("ext4")
            xfs_attempted = True
            self.run_fs("xfs")
        except (RuntimeError, subprocess.TimeoutExpired) as error:
            self.results["error"] = str(error)
        finally:
            if xfs_attempted:
                self.try_cleanup(cleanup_errors, self.cleanup_xfs)
            preserve_xfs = xfs_attempted and bool(cleanup_errors)
            if preserve_xfs:
                self.log("cleanup", "XFS cleanup could not be verified. Preserving the run's resources and backing volume.")
            if configured and not preserve_xfs:
                self.try_cleanup(cleanup_errors, lambda: self.dc("cleanup", "down", "-v"))
            if not preserve_xfs:
                self.try_cleanup(cleanup_errors, self.remove_image)
            for kind, listing in (("containers", ("ps", "-a")), ("networks", ("network", "ls")),
                                  ("volumes", ("volume", "ls")), ("images", ("image", "ls"))):
                self.try_cleanup(cleanup_errors, lambda: self.check_removed(kind, listing))
            after = self.try_cleanup(cleanup_errors, lambda: self.collect("after"))
            diff = None if after is None else {
                name: {"removed": sorted(set(before[name]) - set(after[name])),
                       "added": sorted(set(after[name]) - set(before[name]))}
                for name in before if before[name] != after[name]
            }
            self.results["cleanup"] = {"errors": cleanup_errors, "diff": diff,
                                       "xfsAttempted": xfs_attempted, "preservedXfsResources": preserve_xfs,
                                       "status": "PASS" if not cleanup_errors and not diff else "FAIL"}
            self.results["runId"] = self.id
            (self.output / "summary.json").write_text(json.dumps(self.results, indent=2), encoding="utf-8")
            print(json.dumps(self.results, indent=2))
        return int(
            self.results["cleanup"]["status"] != "PASS" or
            any(fs not in self.results or self.results[fs]["suite"] != "PASS" or
                self.results[fs]["filesystem"] != "PASS" or
                (not self.final and [case["status"] for case in self.results[fs]["restart"]] != ["PASS"] * 3)
                for fs in ("ext4", "xfs"))
        )

    @staticmethod
    def try_cleanup(errors, action):
        try:
            return action()
        except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
            errors.append(str(error))

    def remove_image(self):
        owned = self.docker("cleanup", "image", "ls", "-q", "--filter", f"label={self.label}")
        if owned.strip():
            self.docker("cleanup", "image", "rm", self.image)

    def check_removed(self, kind, listing):
        remaining = self.docker("cleanup", *listing, "-q", "--filter", f"label={self.label}")
        if remaining.strip():
            raise RuntimeError(f"Owned {kind} remain: {remaining.strip()}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True, help="self-contained linux-x64 harness directory")
    parser.add_argument("--output", type=Path, required=True, help="new directory for run logs")
    parser.add_argument("--final", action="store_true", help="one process crash per seam, skip container restarts")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Run from Windows against its local Docker Desktop WSL engine.")
    raise SystemExit(Run(args.publish, args.output, args.final).run())
