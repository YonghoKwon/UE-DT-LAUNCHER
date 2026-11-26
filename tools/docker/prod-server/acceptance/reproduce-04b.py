"""Run the PROD-04B Docker SSH acceptance on Windows with Python 3.12 and psutil."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import locale
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import uuid
import zipfile

import psutil


def execute(*argv, cwd=None, timeout=180):
    return subprocess.run(list(map(str, argv)), cwd=cwd, capture_output=True,
                          text=True, encoding="utf-8", errors="replace", timeout=timeout)


def decode_cli(data):
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode(locale.getencoding(), errors="strict")


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def utc():
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def archive(package, expected, target, method):
    with zipfile.ZipFile(target, "x", method, compresslevel=1) as output:
        for name in expected:
            output.write(package / name, name)
    with zipfile.ZipFile(target) as source:
        require(set(source.namelist()) == set(expected), "ZIP inventory mismatch")
        for name, item in expected.items():
            with source.open(name) as content:
                require(hashlib.file_digest(content, "sha256").hexdigest() == item["sha256"],
                        "ZIP member hash mismatch")


class Acceptance:
    def __init__(self, args):
        from prepare_prod_server import Fixture, published

        self.args = args
        self.id = "p04b" + uuid.uuid4().hex[:12]
        self.out = args.output / "evidence" / self.id
        self.private = args.private / self.id
        self.out.mkdir(parents=True)
        self.private.mkdir(parents=True)
        self.fixture = Fixture(argparse.Namespace(run_id=self.id, work=self.private / "fixture"))
        self.track = ("--project-id", "ma0t10-demo", "--environment", "prod", "--channel", "stable",
                      "--platform", "windows-x64")
        self.report = {"runId": self.id, "head": args.head, "cases": {}, "gates": {}, "commands": []}
        self.before = None
        self.source = None
        self.win = read_json(args.win_cohort)
        self.linux = read_json(args.linux_cohort)
        for cohort, rid in ((self.win, "win-x64"), (self.linux, "linux-x64")):
            require(cohort["sourceHead"] == args.head and cohort["rid"] == rid and
                    cohort["success"] and cohort["productSourceDirty"] is False, "Publication source/RID mismatch")
        self.launcher = published(self.win, "launcher")
        published(self.linux, "server")
        self.identity = self.private / "uploader"
        self.hosts = self.private / "known_hosts"
        self.profile = self.private / "upload.profile.json"
        self.prj = self.private / "project.json"
        self.report["publication"] = {
            "windowsLauncherSha256": self.win["binaries"]["launcher"]["sha256"],
            "linuxServerSha256": self.linux["binaries"]["server"]["sha256"],
            "sdk": self.win["sdkVersion"],
            "productSourceHash": self.win["productSourceHash"],
        }
        self.report["startedAtUtc"] = utc()

    def record(self):
        save(self.out / "integration.json", self.report)

    def case(self, name, **values):
        self.report["cases"][name] = values
        self.record()

    def gate(self, number, status, **metrics):
        self.report["gates"][f"G{number}"] = {"status": status, **metrics}
        self.record()

    def verify(self, number, condition, message, **metrics):
        self.gate(number, "PASS" if condition else "FAIL", **metrics)
        require(condition, message)

    def docker(self, *argv):
        from prepare_prod_server import run
        return run("docker", *argv)

    def snapshot(self, label):
        rows = {
            "containers": self.docker("ps", "-a", "--no-trunc", "--format", "{{.ID}} {{.Names}}"),
            "networks": self.docker("network", "ls", "--no-trunc", "--format", "{{.ID}} {{.Name}}"),
            "volumes": self.docker("volume", "ls", "--format", "{{.Driver}} {{.Name}}"),
            "images": self.docker("image", "ls", "-a", "--no-trunc", "--format", "{{.ID}} {{.Repository}}:{{.Tag}}"),
        }
        for name, content in rows.items():
            (self.out / f"{label}-{name}.txt").write_text(content + "\n", encoding="utf-8")
        (self.out / f"{label}-df.txt").write_text(self.docker("system", "df") + "\n", encoding="utf-8")
        return {name: set(content.splitlines()) for name, content in rows.items()}

    def preflight(self):
        running = self.docker("ps", "--format", "{{.ID}} {{.Names}} {{.Ports}}").splitlines()
        self.case("preflight", runningContainers=running, dockerContext=self.docker("context", "show"))
        require(not any("uedt-prod" in row for row in running), "Another UE DT trial is running")
        for address, port in (("192.168.0.2", 18080), ("127.0.0.1", 12222)):
            with socket.socket() as probe:
                probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                try:
                    probe.bind((address, port))
                except OSError as exc:
                    raise RuntimeError(f"Preflight port {address}:{port} unavailable (OS {exc.errno})") from exc
        self.before = self.snapshot("before")

    def setup(self):
        from prepare_versioned_real_ue_fixture import inventory, fingerprint

        self.source = inventory(self.args.ue_package)
        self.case("ueSource", files=len(self.source), bytes=sum(row["size"] for row in self.source.values()),
                  inventorySha256=fingerprint(self.source))
        require("ma0t10_dt.exe" in self.source, "ma0t10 entry point is missing")
        self.big_zip = self.private / "ma0t10-stored.zip"
        self.small_zip = self.private / "small-fixture.zip"
        archive(self.args.ue_package, self.source, self.big_zip, zipfile.ZIP_STORED)
        with zipfile.ZipFile(self.small_zip, "x", zipfile.ZIP_DEFLATED) as output:
            output.writestr("ma0t10_dt.exe", b"Local PROD-04B publication fixture, never executed\n")
        require(execute("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", self.id,
                        "-f", self.identity).returncode == 0, "Uploader key generation failed")
        self.preflight()
        self.fixture.stage(self.linux, self.identity.with_suffix(".pub"))
        self.fixture.up(self.args.server_multi)
        self.sshd = self.fixture.compose("ps", "-q", "sshd")
        self.dist = self.fixture.compose("ps", "-q", "dist")
        for source in ("image-runtime.json", "compose-ps.json", "elf-readelf.txt"):
            data = (self.fixture.work / source).read_bytes()
            (self.out / source).write_bytes(data)
        self.case("stack", dist=self.dist, sshd=self.sshd,
                  ports=self.docker("ps", "--filter", f"label=uedt.run={self.id}",
                                    "--format", "{{.Names}} {{.Ports}}").splitlines())
        self.pin_host()
        save(self.profile, {"server": "uploader@127.0.0.1", "port": 12222,
                            "identityFile": str(self.identity), "knownHostsFile": str(self.hosts),
                            "remoteIncoming": "/dt/store/incoming", "remoteStatus": "/dt/store/upload-status"})
        save(self.prj, {"schemaVersion": 1, "projectId": "ma0t10-demo", "platform": "windows-x64",
                        "entryPoint": "ma0t10_dt.exe", "environment": "prod", "channel": "stable"})

    def pin_host(self):
        declared = self.docker("exec", self.sshd, "ssh-keygen", "-lf", "/dt/tools/ssh_host_ed25519_key.pub")
        scan = execute("ssh-keyscan", "-T", "5", "-t", "ed25519", "-p", "12222", "127.0.0.1")
        require(scan.returncode == 0 and scan.stdout.startswith("[127.0.0.1]:12222 ssh-ed25519 "),
                "Cannot obtain the owned sshd host key")
        self.hosts.write_bytes(scan.stdout.encode("ascii"))
        observed = execute("ssh-keygen", "-lf", self.hosts)
        require(observed.returncode == 0, "Cannot fingerprint the dedicated known_hosts file")
        pattern = r"SHA256:[A-Za-z0-9+/]+"
        want = re.findall(pattern, declared)
        got = re.findall(pattern, observed.stdout)
        require(len(want) == 1 and want == got, "Owned sshd and loopback fingerprints differ")
        self.case("hostKey", pinned=want[0])

    def direct_ssh(self, *argv, scp=False):
        # A private known_hosts file and no local configuration changes.
        opt = ("-o", "BatchMode=yes", "-o", "UpdateHostKeys=no", "-o", "StrictHostKeyChecking=yes",
               "-o", f'UserKnownHostsFile="{self.hosts.as_posix()}"', "-o", "IdentitiesOnly=yes",
               "-o", "ConnectTimeout=10", "-i", self.identity, "-P" if scp else "-p", "12222")
        tool, prefix = ("scp", ()) if scp else ("ssh", ("-n", "-T", "uploader@127.0.0.1"))
        return execute(tool, *opt, *prefix, *argv, cwd=self.private, timeout=40)

    @staticmethod
    def status_remote(ident):
        directory = "/dt/store/upload-status"
        path = f"{directory}/{ident}.json"
        return (f"if test -r {directory} && test -x {directory} && ! test -e {path} && ! test -L {path}; "
                f"then exit 44; fi; exec cat -- {path}")

    def cli_status(self, ident, *flags, trace=False, on_attempt=None):
        cmd = [self.launcher, "upload", "status", "--profile", self.profile,
               "--upload-id", ident, "--format", "json", *flags]
        started, begin_utc = time.monotonic(), utc()
        attempts = []
        if trace:
            with subprocess.Popen(list(map(str, cmd)), cwd=self.private,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
                ps = psutil.Process(process.pid)
                seen = set()
                while process.poll() is None:
                    try:
                        children = ps.children()
                    except psutil.NoSuchProcess:
                        break
                    for child in children:
                        try:
                            name = child.name().lower()
                        except psutil.NoSuchProcess:
                            continue
                        if name == "ssh.exe" and child.pid not in seen:
                            seen.add(child.pid)
                            attempts.append({"observedAtUtc": utc(), "elapsedSeconds": round(time.monotonic()-started, 3)})
                            if on_attempt is not None:
                                on_attempt(len(attempts))
                    if time.monotonic()-started > 140:
                        process.kill()
                        raise RuntimeError("Status process exceeded the harness timeout")
                    time.sleep(0.005)
                stdout, stderr = (decode_cli(stream) for stream in process.communicate())
                code = process.returncode
        else:
            result = subprocess.run(list(map(str, cmd)), cwd=self.private, capture_output=True, timeout=140)
            code, stdout, stderr = result.returncode, decode_cli(result.stdout), decode_cli(result.stderr)
        doc = json.loads(stdout) if stdout.strip() else None
        # Use allowlisted evidence for errors and status documents.
        if doc:
            allowed = {"schemaVersion", "uploadId", "state", "label", "updatedAtUtc", "queriedAtUtc", "reason", "release"}
            if set(doc) - allowed or doc.get("uploadId") != ident:
                raise RuntimeError("Status response contains a non-public field or upload ID mismatch")
        return {"command": ["upload", "status", "--profile", "<isolated profile>",
                            "--upload-id", ident, "--format", "json", *flags],
                "startUtc": begin_utc, "endUtc": utc(), "exit": code,
                "seconds": round(time.monotonic()-started, 3), "status": doc,
                "sshAttemptsObserved": attempts, "stderrPresent": bool(stderr.strip())}

    def upload(self, ident, version, archive_path, watch=False):
        args = [self.launcher, "upload", "--profile", self.profile, "--project-profile", self.prj,
                "--upload-id", ident, "--zip", archive_path, "--version", version, "--format", "json"]
        plan_hash = None
        if watch:
            plan = subprocess.run(list(map(str, [*args, "--dry-run"])), cwd=self.private,
                                  capture_output=True, timeout=180)
            metadata = json.loads(decode_cli(plan.stdout)) if plan.stdout else {}
            plan_hash = metadata.get("sidecarSha256")
            require(plan.returncode == 0 and plan_hash, "Product dry-run did not produce the sidecar hash")
        start = time.monotonic()
        transferring = None
        polls = []
        with subprocess.Popen(list(map(str, args)), cwd=self.private,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE) as process:
            ps = psutil.Process(process.pid)
            while watch and process.poll() is None:
                observation = self.cli_status(ident)
                try:
                    children = [child.name().lower() for child in ps.children()]
                except psutil.NoSuchProcess:
                    children = []
                polls.append({"elapsedSeconds": round(time.monotonic()-start, 3),
                              "exit": observation["exit"], "state": (observation["status"] or {}).get("state"),
                              "scpActive": "scp.exe" in children})
                if observation["exit"] == 0 and observation["status"]["state"] == "transferring" and "scp.exe" in children:
                    transferring = observation
                    break
                if time.monotonic()-start > 150:
                    process.kill()
                    raise RuntimeError("Transfer exceeded the harness timeout")
                time.sleep(0.08)
            try:
                stdout, _ = process.communicate(timeout=300)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate()
                raise RuntimeError("Upload exceeded the harness timeout")
            elapsed = round(time.monotonic()-start, 3)
        result = json.loads(decode_cli(stdout)) if stdout.strip() else {}
        self.case(f"upload-{version}", exit=process.returncode, uploadId=ident, version=version,
                  zipBytes=archive_path.stat().st_size, seconds=elapsed, failedStage=result.get("failedStage"),
                  transferPolls=polls, transferring=transferring, sidecarSha256=plan_hash)
        require(process.returncode == 0 and result["success"] and result["uploadId"] == ident,
                f"Product upload failed at {result.get('failedStage')}")
        wait = self.cli_status(ident, "--wait", "--timeout", "100", "--interval", "1", trace=True)
        self.case(f"pending-{version}", **wait)
        if watch and (wait["exit"] != 0 or (wait["status"] or {}).get("state") != "pending"):
            self.gate(3, "FAIL", seconds=wait["seconds"], exit=wait["exit"])
        require(wait["exit"] == 0 and (wait["status"] or {}).get("state") == "pending",
                f"Upload {version} did not reach pending")
        return transferring, wait

    def projection(self, name, ident, expected):
        limit = time.monotonic() + 20
        while time.monotonic() < limit:
            value = self.cli_status(ident)
            if value["exit"] == 0 and value["status"]["state"] == expected:
                self.case(name, **value)
                return value
            time.sleep(0.2)
        self.case(name, **value)
        raise RuntimeError(f"{name}: expected {expected}, got {(value['status'] or {}).get('state')}")

    def remote_inventory(self, ident, version):
        from prepare_prod_server import digest
        incoming = f"/dt/store/incoming/{ident}"
        output = self.docker("exec", self.dist, "bash", "-ec",
                             'cd "$1"; find . -type f -print0 | sort -z | xargs -0 -r sha256sum',
                             "bash", incoming)
        actual = {name: hash_ for hash_, name in (row.split(maxsplit=1) for row in output.splitlines())}
        want = {"./payload.zip": digest(self.big_zip), "./release.json": self.report["cases"][f"upload-{version}"]["sidecarSha256"]}
        self.case("primaryPair", uploadId=ident, version=version, remote=actual, local=want, exactPair=actual == want)
        require(actual == want, "Primary final names or hashes differ")

    def admin(self, *args):
        result = self.fixture.compose("run", "--rm", "--no-deps", "-T", "dist", "admin", *args)
        self.report["commands"].append(["admin", *("<job id>" if i == 1 and args[0] in ("approve", "reject") else arg
                                                      for i, arg in enumerate(args))])
        self.record()
        return result

    def job(self, ident):
        jobs = json.loads(self.fixture.server("list"))
        row = next((job for job in jobs if job["source"] == f"/dt/store/incoming/{ident}"), None)
        require(row is not None, "Matching job missing from admin list")
        return row["id"]

    def promote(self, version):
        previous = json.loads(self.admin("promotion", "inspect", *self.track))["revision"]
        self.admin("promote", *self.track, "--version", version, "--expected-revision", str(previous),
                   "--reason", "Local PROD-04B acceptance")
        return previous

    def flow(self):
        self.active_gate = 2
        fallback = []
        for attempt in range(4):
            version = f"1.1.{attempt}"
            suffix = "-primary" if attempt == 0 else f"-transfer-retry-{attempt}"
            primary = self.id + suffix
            transferring, pending = self.upload(primary, version, self.big_zip, watch=True)
            if attempt:
                fallback.append({"attempt": attempt, "uploadId": primary, "version": version,
                                 "observed": bool(transferring)})
            if transferring is not None:
                break

        self.gate(3, "PASS", uploadId=primary, seconds=pending["seconds"], exit=0)
        self.remote_inventory(primary, version)
        self.admin("approve", self.job(primary))
        published = self.projection("primary-published", primary, "published")
        revision_a = self.promote(version)
        recommended = self.projection("primary-recommended", primary, "recommended")
        secondary = self.id + "-secondary"
        self.upload(secondary, "1.2.0", self.small_zip)
        self.admin("approve", self.job(secondary))
        self.projection("secondary-published", secondary, "published")
        revision_b = self.promote("1.2.0")
        self.projection("secondary-recommended", secondary, "recommended")
        superseded = self.projection("primary-superseded", primary, "published")
        require((revision_a, revision_b) == (0, 1), "Promotion expected revisions differ")

        sequence = [row for row in (transferring, pending, published, recommended, superseded)
                    if row is not None]
        require(all(row["status"]["uploadId"] == primary for row in sequence),
                "G2 observations belong to different upload IDs")
        states = [row["status"]["state"] for row in sequence]
        expected = ["pending", "published", "recommended", "published"]
        require(states == (["transferring"] + expected if transferring else expected),
                "G2 observations are out of order")
        self.gate(2, "PASS" if transferring else "NOT_RUN", uploadId=primary, version=version,
                  stateTransitions=len(sequence), observations=sequence,
                  fallbacks=fallback, promotionExpectedRevisions=[revision_a, revision_b])
        return primary

    def bad_archive(self, ident):
        from prepare_prod_server import digest
        bad = self.private / "invalid-archive"
        bad.mkdir()
        payload = bad / "payload.zip.uploading"
        payload.write_bytes(b"PROD-04B bad ZIP fixture without a central directory\n")
        save(bad / "release.json.uploading", {
            "schemaVersion": 1, "packageFile": "payload.zip", "packageSize": payload.stat().st_size,
            "packageSha256": digest(payload), "projectId": "ma0t10-demo", "displayName": "PROD-04B",
            "version": "1.3.0", "environment": "prod", "channel": "stable", "platform": "windows-x64",
            "payloadRoot": ".", "entryPoint": "ma0t10_dt.exe", "executablePaths": [],
        })
        folder = f"/dt/store/incoming/{ident}"
        require(self.direct_ssh(f"mkdir -- {folder}").returncode == 0, "Bad ZIP staging mkdir failed")
        for filename in ("payload.zip", "release.json"):
            remote = f"{folder}/{filename}"
            result = self.direct_ssh(bad / (filename + ".uploading"),
                                     f"uploader@127.0.0.1:{remote}.uploading", scp=True)
            require(result.returncode == 0, "Bad ZIP staging SFTP failed")
            result = self.direct_ssh(f"mv -n -- {remote}.uploading {remote} && test ! -e {remote}.uploading && test -f {remote}")
            require(result.returncode == 0, "Bad ZIP staging final rename failed")
        self.case("badZipStage", auth="pinned OpenSSH", zipBytes=payload.stat().st_size, zipSha256=digest(payload),
                  matchingSidecarHash=True, payloadType="invalid ZIP central directory", exit=0)

    def negatives(self):
        self.active_gate = 4
        failed = self.id + "-bad-zip"
        self.bad_archive(failed)
        failure = self.cli_status(failed, "--wait", "--timeout", "60", "--interval", "1", trace=True)
        rejected = self.id + "-reject"
        self.upload(rejected, "1.4.0", self.small_zip)
        rejected_job = self.job(rejected)
        self.admin("reject", rejected_job)
        self.projection("rejected-single-query", rejected, "rejected")
        rejection = self.cli_status(rejected, "--wait", "--timeout", "60", "--interval", "1", trace=True)
        expectation = [
            (failure, "failed", "서버의 패키지 검사에 실패했습니다. 운영자에게 검사 결과 확인을 요청하세요.", self.job(failed)),
            (rejection, "rejected", "운영자가 업로드를 거절했습니다. 운영자에게 확인하세요.", rejected_job),
        ]
        for observation, state, reason, job_id in expectation:
            actual = observation["status"] or {}
            content = json.dumps(actual, ensure_ascii=False)
            require(observation["exit"] == 3 and actual.get("state") == state and actual.get("reason") == reason,
                    f"{state} status or fixed reason differed")
            require("release" not in actual and not any(token in content for token in
                    (job_id, "/dt/", "Authorization", "PRIVATE KEY", "ssh-ed25519", "Signature")),
                    f"{state} status exposed an internal field")
            self.case(state, **observation)
        self.gate(4, "PASS", fixedKoreanReasons=2, sensitiveFields=0)

    def store_hashes(self, phase):
        command = "cd /dt; find store config keys -type f -print0 | sort -z | xargs -0 -r sha256sum --zero"
        result = execute("docker", "exec", self.dist, "bash", "-o", "pipefail", "-euc", command)
        self.case(f"store-hash-process-{phase}", exit=result.returncode,
                  pipefail=True, stderrPresent=bool(result.stderr.strip()))
        require(result.returncode == 0 and not result.stderr.strip(),
                f"Could not collect complete {phase} G6 hashes")
        groups = {"server": [], "status": []}
        for row in result.stdout.split("\0")[:-1]:
            require(re.fullmatch(r"[a-f0-9]{64}  (?:store|config|keys)/.+", row, flags=re.DOTALL) is not None,
                    f"Invalid {phase} G6 hash record")
            name = row[66:]
            groups["status" if name.startswith("store/upload-status/") else "server"].append(row)
        require(result.stdout.endswith("\0") and all(groups.values()),
                f"Missing {phase} G6 hash records")
        # Keep aggregate digests and counts only, without names or individual key hashes.
        return {key: {"files": len(items), "sha256": hashlib.sha256("\0".join(items).encode()).hexdigest()}
                for key, items in groups.items()}

    def exit_and_recovery(self, ident):
        self.active_gate = 5
        missing = self.id + "-never-created"
        raw = self.direct_ssh(self.status_remote(missing))
        self.case("raw-missing", exit=raw.returncode, stdoutBytes=len(raw.stdout))
        normal = self.cli_status(ident, trace=True)
        once = self.cli_status(missing, trace=True)
        invalid = self.cli_status("invalid/id", trace=True)
        expiry = self.cli_status(missing, "--wait", "--timeout", "5", "--interval", "1", trace=True)
        for name, case in (("normal", normal), ("missing", once), ("invalid", invalid), ("timeout", expiry)):
            self.case(f"exit-{name}", **case)
        require(raw.returncode == 44 and raw.stdout == "" and
                [normal["exit"], once["exit"], invalid["exit"], expiry["exit"]] == [0, 1, 2, 4] and
                len(invalid["sshAttemptsObserved"]) == 0 and len(expiry["sshAttemptsObserved"]) > 1,
                "Normal, missing, argument or timeout exit contract differed")
        self.fixture.compose("stop", "sshd")
        try:
            disconnected = self.cli_status(ident, "--wait", "--timeout", "60", "--interval", "1", trace=True)
            self.case("exit-three-ssh-failures", **disconnected)
        finally:
            self.fixture.compose("start", "sshd")
        require(disconnected["exit"] == 1 and len(disconnected["sshAttemptsObserved"]) == 3,
                "Consecutive SSH failure did not produce three attempts and CLI exit 1")
        self.gate(5, "PASS", cliExitCodes=[0, 1, 2, 3, 4], missingSshExit=44, consecutiveSshFailures=3,
                  consecutiveFailureCliExit=1, timeoutSeconds=expiry["seconds"],
                  timeoutSshAttempts=len(expiry["sshAttemptsObserved"]))

        self.active_gate = 6
        stable = self.projection("before-outage", ident, "published")
        before = self.store_hashes("before")
        self.fixture.compose("stop", "sshd")
        restarted = False

        def resume_after_failure(count):
            nonlocal restarted
            # Starting as the second attempt launches leaves the first attempt
            # conclusively failed and allows the second or third to recover.
            if count == 2 and not restarted:
                self.fixture.compose("start", "sshd")
                restarted = True

        try:
            recovery = self.cli_status(ident, "--wait", "--timeout", "65", "--interval", "2",
                                       trace=True, on_attempt=resume_after_failure)
        finally:
            if not restarted:
                self.fixture.compose("start", "sshd")
        after = self.store_hashes("after")
        self.case("recovery", **recovery)
        self.case("outageHashes", before=before, after=after)
        baseline = {k: v for k, v in stable["status"].items() if k != "queriedAtUtc"}
        actual = {k: v for k, v in (recovery["status"] or {}).items() if k != "queriedAtUtc"}
        attempts = len(recovery["sshAttemptsObserved"])
        self.verify(6, restarted and recovery["exit"] == 0 and attempts in (2, 3) and baseline == actual and before == after,
                    "SSH recovery did not preserve the status or repository hashes",
                    sshFailures=attempts-1, totalSshAttempts=attempts, hashChanges=0 if before == after else 1,
                    seconds=recovery["seconds"])

    def permissions(self, ident):
        self.active_gate = 7
        inspect = json.loads(self.docker("inspect", self.sshd))[0]
        mounts = {entry["Destination"]: {"rw": entry["RW"], "volume": entry["Name"]}
                  for entry in inspect["Mounts"]}
        prefix = "/dt/upload-jail/dt/store"
        expect = {prefix + "/incoming": {"rw": True, "volume": self.fixture.project + "_store"},
                  prefix + "/upload-status": {"rw": False, "volume": self.fixture.project + "_store"}}
        subpaths = {entry["Target"]: entry["VolumeOptions"]["Subpath"] for entry in inspect["HostConfig"]["Mounts"]}
        dist = json.loads(self.docker("inspect", self.dist))[0]
        dist_store = next((mount for mount in dist["Mounts"] if mount["Destination"] == "/dt/store"), {})
        original_store_writable = dist_store.get("RW") is True and dist_store.get("Name") == self.fixture.project + "_store"
        mode = self.fixture.dist("stat", "-c", "%a %U:%G", "/dt/store/incoming", "/dt/store/upload-status",
                                 f"/dt/store/upload-status/{ident}.json", "/dt/store/authority", "/dt/store/releases",
                                 "/dt/store/processing", "/dt/store/archive", "/dt/config", "/dt/keys/signing.pem").splitlines()
        self.case("permission-mounts", mounts=mounts, subpaths=subpaths, distModes=mode,
                  originalStoreWritable=original_store_writable,
                  portBindings=inspect["HostConfig"]["PortBindings"])
        require(original_store_writable and mounts == expect and subpaths == {path: path.rsplit("/", 1)[1] for path in expect} and
                inspect["HostConfig"]["PortBindings"] == {"22/tcp": [{"HostIp": "127.0.0.1", "HostPort": "12222"}]},
                "Unexpected sshd mounts, subpaths or port bindings")

        jail_root = "/dt/upload-jail"
        status_file = f"/dt/store/upload-status/{ident}.json"
        probe_folder = f"/dt/store/incoming/{ident}-permission"
        paths = {
            "authority": ("cat", "/dt/store/authority/HEAD"),
            "releases": ("ls", "/dt/store/releases"),
            "processing": ("ls", "/dt/store/processing"),
            "archive": ("ls", "/dt/store/archive"),
            "config": ("cat", "/dt/config/server.json"),
            "keys": ("cat", "/dt/keys/signing.pem"),
        }
        writes = {
            "status-overwrite": f": >> {status_file}",
            "status-create": "/usr/bin/touch /dt/store/upload-status/.p04b-write-probe",
            "status-delete": f"/usr/bin/rm -- {status_file}",
            "store-create": "/usr/bin/touch /dt/store/.p04b-write-probe",
        }
        # Root checks only existence. All access and write probes run with uploader's UID/GID.
        original_paths = [path for _, path in paths.values()] + [status_file, "/dt/store/upload-status"]
        self.fixture.dist("bash", "-euc", 'for path; do test -e "$path"; done', "bash", *original_paths)
        self.case("permission-original-paths", checked=original_paths, allPresent=True)
        commands = {name: f"/usr/bin/{tool} {path}" for name, (tool, path) in paths.items()} | writes
        for name, remote in commands.items():
            ssh = self.direct_ssh(remote)
            denied = ssh.returncode == 126 and "SSH command not permitted" in ssh.stderr and not ssh.stdout
            self.case(f"permission-ssh-{name}", exit=ssh.returncode, forcedCommandDenied=denied)
            require(denied, f"SSH prohibition failed for {name}")

        for name, remote in commands.items():
            jail_command = remote.replace("/dt/", jail_root + "/dt/")
            jail_expected = ("ENOENT",) if name in paths else ("EACCES", "EROFS")
            self.uid_probe("jail", self.sshd, name, jail_command, jail_expected)
            self.uid_probe("original", self.dist, name, remote, ("EACCES",))
        self.uid_probe("original", self.dist, "read-status", f"/usr/bin/cat {status_file}", ("OK",))

        positive = self.direct_ssh(self.status_remote(ident))
        require(positive.returncode == 0 and json.loads(positive.stdout)["uploadId"] == ident,
                "Uploader could not read its status through the forced SSH command")
        self.case("permission-read-status", exit=positive.returncode)
        directory = self.direct_ssh(f"mkdir -- {probe_folder}")
        probe = self.private / "permission-probe"
        probe.write_bytes(b"PROD-04B private fixture probe\n")
        upload = self.direct_ssh(probe, f"uploader@127.0.0.1:{probe_folder}/payload.zip.uploading", scp=True)
        remote_size = self.fixture.dist("stat", "-c", "%s", probe_folder + "/payload.zip.uploading")
        require(directory.returncode == 0 and upload.returncode == 0 and int(remote_size) == probe.stat().st_size,
                "Uploader could not write into its own incoming folder")
        self.case("permission-write-incoming", mkdirExit=directory.returncode, scpExit=upload.returncode, bytes=int(remote_size))
        require([line.split()[0] for line in mode] == ["2770", "750", "640", "700", "700", "700", "700", "750", "400"],
                "On-disk permission modes differ from the fixture contract")
        self.gate(7, "PASS", sshDenied=len(commands), jailDenied=len(commands), originalDenied=len(commands),
                  originalUid=20002, forbiddenReads=len(paths), forbiddenWrites=len(writes),
                  allowedOperations=2, sshdMounts=len(mounts))

    def uid_probe(self, scope, container, name, command, expected):
        identity = 'test "$(id -u):$(id -g):$(id -G)" = 20002:20002:20002 || exit 90; '
        result = execute("docker", "exec", "-e", "LC_ALL=C", container, "setpriv", "--reuid=20002",
                         "--regid=20002", "--clear-groups", "--no-new-privs", "/bin/bash", "-ec",
                         identity + "{ " + command + "; } >/dev/null")
        errors = {"Permission denied": "EACCES", "Read-only file system": "EROFS", "No such file or directory": "ENOENT"}
        signals = [code for message, code in errors.items() if message in result.stderr]
        if result.returncode == 0 and not result.stderr:
            outcome = "OK"
        elif result.returncode in (1, 2) and len(signals) == 1:
            outcome = signals[0]
        else:
            outcome = "UNEXPECTED"
        self.case(f"permission-{scope}-{name}", exit=result.returncode, outcome=outcome, uid=20002, gid=20002)
        require(outcome in expected and not result.stdout, f"Uploader {scope} probe for {name} returned {outcome}")

    def cleanup(self):
        if self.fixture.ownership:
            self.fixture.down()
            (self.out / "docker-residual.json").write_bytes((self.fixture.work / "docker-residual.json").read_bytes())
        if self.before is not None:
            after = self.snapshot("after")
            delta = {name: {"added": sorted(after[name] - items), "removed": sorted(items - after[name])}
                     for name, items in self.before.items()}
            self.gate(8, "PASS" if self.before == after else "FAIL", resourceDelta=delta)
        if self.source is not None:
            from prepare_versioned_real_ue_fixture import fingerprint, inventory
            actual = inventory(self.args.ue_package)
            self.report["cases"]["ueSource"]["postInventorySha256"] = fingerprint(actual)
            require(actual == self.source, "Original package changed during validation")

    def run(self):
        self.active_gate = 0
        try:
            self.setup()
            primary = self.flow()
            self.negatives()
            self.exit_and_recovery(primary)
            self.permissions(primary)
        except Exception as exc:
            self.report["failure"] = {"gate": self.active_gate, "type": type(exc).__name__,
                                      "summary": str(exc).splitlines()[0].replace(str(self.private), "<private>")}
            if self.active_gate and f"G{self.active_gate}" not in self.report["gates"]:
                self.gate(self.active_gate, "FAIL")
        finally:
            try:
                self.cleanup()
            except Exception as exc:
                self.report["cleanupError"] = {"type": type(exc).__name__}
                if "G8" not in self.report["gates"]:
                    self.gate(8, "FAIL")
            for n in range(2, 9):
                self.report["gates"].setdefault(f"G{n}", {"status": "NOT_RUN"})
            self.report["finishedAtUtc"] = utc()
            self.record()
            print(json.dumps({"runId": self.id, "failure": bool(self.report.get("failure")),
                              "gates": {k: v["status"] for k, v in self.report["gates"].items()}}))
        return 0 if all(v["status"] == "PASS" for v in self.report["gates"].values()) and not (
            self.report.get("failure") or self.report.get("cleanupError")) else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repo", "output", "private", "ue-package", "win-cohort", "linux-cohort", "server-multi"):
        parser.add_argument("--" + name, type=lambda value: Path(value).resolve(), required=True)
    parser.add_argument("--head", required=True)
    args = parser.parse_args()
    require(os.name == "nt", "This acceptance uses the approved Windows Docker binding")
    require(execute("git", "rev-parse", "HEAD", cwd=args.repo).stdout.strip() == args.head,
            "Repository HEAD differs from the publication source")
    require(execute("git", "status", "--porcelain", cwd=args.repo).stdout == "", "Use a clean fixture source")
    sys.path.insert(0, str(args.repo / "tools"))
    return Acceptance(args).run()


if __name__ == "__main__":
    sys.exit(main())
