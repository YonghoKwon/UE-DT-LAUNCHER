"""PROD-06B: run from repository root, with an exact-SHA published cohort."""
import argparse
import hashlib
import importlib.util
import ipaddress
import json
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("prod01d", Path.cwd() / "tools/prepare_prod_server.py")
prod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prod)
H = "/dt/tools/server-home"
POLICY = "/dt/config/access-policy.json"
POLICY_ACL_ENTRY = "user:20003:r--"
KEYS = "/dt/keys"
BUNDLES = KEYS + "/enroll"
SERVER = "/dt/publish/server-linux-x64/UeDtLauncher.DistributionServer"
ROLES = "abcde"
INV = {
    "containers": ("ps", "-a", "--no-trunc", "--format", "{{.ID}} {{.Names}}"),
    "networks": ("network", "ls", "--no-trunc", "--format", "{{.ID}} {{.Name}}"),
    "volumes": ("volume", "ls", "--format", "{{.Name}}"),
    "images": ("image", "ls", "--no-trunc", "--format", "{{.ID}} {{.Repository}}:{{.Tag}}"),
}


class Run:
    def __init__(self, args):
        self.report, self.priv = args.report.resolve(), args.private.resolve()
        self.work = self.priv / "stack"
        self.fx = prod.Fixture(argparse.Namespace(work=self.work, run_id=args.run_id))
        self.overlay = self.work / "enrollment.compose.json"
        self.network = self.fx.project + "_default"
        self.evidence = self.report / "evidence"
        self.logs = self.report / "logs"
        self.evidence.mkdir(parents=True, exist_ok=True)
        self.logs.mkdir(parents=True, exist_ok=True)
        self.gates_path = self.evidence / "gate-observations.json"
        self.gates = json.loads(self.gates_path.read_text()) if self.gates_path.exists() else {}

    def save(self, name, data):
        (self.evidence / name).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")

    def gate(self, number, status, **data):
        self.gates[f"G{number}"] = {"status": status, **data}
        self.save(self.gates_path.name, self.gates)

    def command(self, label, args, expected=None, input_data=None):
        cmd = [str(v) for v in args]
        if expected is not None:
            print(label, flush=True)
        result = subprocess.run(cmd, capture_output=True, input=input_data, timeout=160)
        stdout, stderr = (v.decode("utf-8", "replace").strip() for v in (result.stdout, result.stderr))
        with (self.evidence / "commands.jsonl").open("a", encoding="utf-8") as f:
            f.write(json.dumps({"step": label, "command": cmd, "exit": result.returncode}) + "\n")
        (self.logs / f"{label}.txt").write_text(stdout + "\n" + stderr + "\n", encoding="utf-8")
        if expected is not None and result.returncode != expected:
            raise RuntimeError(f"{label}: exit {result.returncode}, expected {expected}")
        return result.returncode, stdout

    def compose(self, *args, **kwargs):
        self.fx.guard_docker()
        return prod.run("docker", "compose", "-p", self.fx.project, "-f", prod.TEMPLATES / "compose.yaml",
                        "-f", self.overlay, *args, **kwargs)

    def inventory(self, when):
        listed = {key: sorted(prod.run("docker", *args).splitlines()) for key, args in INV.items()}
        self.save(f"docker-{when}.json", listed)
        (self.evidence / f"docker-df-{when}.txt").write_text(prod.run("docker", "system", "df") + "\n")
        return listed

    def exclusive(self):
        active = set(prod.run("docker", "ps", "-q", "--no-trunc").splitlines())
        own = set()
        if self.fx.ownership:
            own = set(prod.run("docker", "ps", "-q", "--no-trunc", "--filter",
                               f"label=uedt.owner={self.fx.ownership['owner']}").splitlines())
        if active - own:
            raise RuntimeError(f"Docker slot unavailable: {sorted(active - own)}")

    def start(self):
        self.inventory("before")
        self.exclusive()
        networks = json.loads(prod.run("docker", "network", "inspect",
                                      *prod.run("docker", "network", "ls", "-q").splitlines()))
        used = [ipaddress.ip_network(row["Subnet"]) for net in networks for row in (net["IPAM"]["Config"] or [])
                if "Subnet" in row and ":" not in row["Subnet"]]
        subnet = next(net for net in ipaddress.ip_network("10.254.0.0/16").subnets(new_prefix=24)
                      if not any(net.overlaps(old) for old in used))
        ips = {role: str(subnet[i + 11]) for i, role in enumerate(ROLES)}
        dist = str(subnet[2])
        linux = json.loads((self.priv / "linux-x64/cohort.json").read_text(encoding="utf-8"))
        self.fx.stage(linux, None)
        # Extend only this run's copy of the pinned Rocky Dockerfile.
        with (self.fx.context / "Dockerfile").open("a", encoding="utf-8", newline="\n") as dockerfile:
            dockerfile.write("\nRUN dnf -y --disablerepo='*' --enablerepo='uedt-8.4-*' "
                             "--setopt=install_weak_deps=False install acl && dnf clean all\n")
        policy = {"clients": [{"id": "sim-c", "addresses": [ips["c"]], "grants": []}]}
        (self.fx.context / "access-policy.json").write_text(json.dumps(policy) + "\n", encoding="utf-8")
        overlay = {
            "networks": {"default": {"ipam": {"config": [{"subnet": str(subnet)}]}}},
            "services": {"dist": {"networks": {"default": {"ipv4_address": dist}}}},
        }
        for role in ROLES:
            overlay["services"][f"sim-{role}"] = {
                "image": self.fx.image, "platform": "linux/amd64", "init": True,
                "profiles": ["enrollment"], "entrypoint": ["sleep", "infinity"],
                "user": "20001:20001", "environment": {"HOME": H, "XDG_DATA_HOME": H + "/xdg"},
                "labels": self.fx.owner_labels, "extra_hosts": ["host.docker.internal:" + dist],
                "networks": {"default": {"ipv4_address": ips[role]}},
            }
        self.overlay.write_text(json.dumps(overlay, indent=2) + "\n", encoding="utf-8")
        self.save("topology.json", {"subnet": str(subnet), "dist": dist, "simulators": ips,
                                   "supplementalMovedIp": str(subnet[31]), "origin": prod.ORIGIN})
        for role in ("store", "config", "keys"):
            self.fx.create_volume(role)
        print("Building the exact 01D Rocky image", flush=True)
        self.compose("build", "dist", timeout=900)
        self.exclusive()
        self.compose("run", "--rm", "--no-deps", "-T", "setup", "seed")
        # Seed a named ACL on the synthetic policy using a fixture-only numeric UID.
        self.compose("run", "--rm", "--no-deps", "-T", "--entrypoint", "setfacl", "setup",
                     "-m", "u:20003:r--", "--", POLICY)
        self.create_bundle_mountpoint()
        self.compose("up", "-d", "--no-build", "--wait", "--wait-timeout", "90", "dist", "nginx", "sshd")
        self.save("stack-services.json", self.fx.ready())
        self.compose("exec", "-T", "nginx", "nginx", "-t")
        self.compose("up", "-d", "--no-build", *(f"sim-{role}" for role in ROLES))
        self.attach()
        self.save("simulator-topology.json", {r: self.fx.container_address(self.ids[r], self.network) for r in ROLES})
        dist_id, nginx_id = (self.compose("ps", "-q", service) for service in ("dist", "nginx"))
        self.save("server-boundaries.json", {
            "mounts": json.loads(prod.run("docker", "inspect", "--format", "{{json .Mounts}}", dist_id)),
            "nginxPeerForward": [line.strip() for line in prod.run("docker", "exec", nginx_id, "cat",
                "/etc/nginx/nginx.conf").splitlines() if "X-Distribution-Client-IP" in line],
            "rpm": self.fx.dist("rpm", "-q", "nginx", "openssh-server", "glibc").splitlines(),
            "linuxPublication": {r: linux["binaries"][r]["sha256"] for r in ("launcher", "server")},
        })
        public = self.priv / "release-public.pem"
        self.fx.compose("cp", f"dist:{KEYS}/release-public.pem", public)
        for role in ROLES:
            prod.run("docker", "cp", public, f"{self.ids[role]}:{H}/release-public.pem")
            prod.run("docker", "exec", "--user", "0", self.ids[role], "chmod", "0644", H + "/release-public.pem")

    def create_bundle_mountpoint(self):
        self.compose("run", "--rm", "--no-deps", "-T", "--entrypoint", "mkdir", "setup", "-p", BUNDLES)

    def attach(self):
        topology = json.loads((self.evidence / "topology.json").read_text())
        self.ips, self.moved = topology["simulators"], topology["supplementalMovedIp"]
        self.ids = {r: self.compose("ps", "-q", f"sim-{r}") for r in ROLES}
        self.exclusive()

    def sim(self, role, label, *args, expected=None):
        return self.command(label, ("docker", "exec", self.ids[role], *args), expected)

    def publish(self):
        win = json.loads((self.priv / "win-x64/cohort.json").read_text(encoding="utf-8"))
        launcher = prod.published(win, "launcher")
        self.save("windows-publication.json", {"sha256": win["binaries"]["launcher"]["sha256"],
                                               "sourceHead": win["sourceHead"]})
        upload = self.work / "06b-linux"
        upload.mkdir()
        payload = upload / "payload.zip"
        with zipfile.ZipFile(payload, "x", zipfile.ZIP_DEFLATED) as z:
            z.writestr("game.sh", "#!/bin/sh\nexit 0\n")
            z.writestr("version.txt", "PROD-06B fixture\n")
        incoming = self.fx.upload(launcher, payload, "demo", "1.0.0", "linux-x64", "game.sh")
        job = self.fx.intake(incoming)
        self.fx.server("approve", job)
        approved = json.loads(self.fx.server("inspect", job))
        assert approved["state"] == "published"
        promoted = self.fx.promote("demo", "linux-x64")
        self.save("publish-demo.json", {"job": job, "state": approved["state"], "promotion": promoted})

    def prepare(self, role, suffix, destination):
        self.sim(role, f"sim-{role}-portable-home-{suffix}", "mkdir", "-m", "700", "-p", H + "/xdg", expected=0)
        directory = f"{H}/public-{suffix}"
        self.sim(role, f"sim-{role}-prepare-{suffix}", prod.LINUX_CLI, "enroll", "prepare",
                 "--storage", "portable", "--name", f"device-{suffix}", "--key-id", f"sim-{role}-{suffix}",
                 "--client", f"sim-{role}", "--output", directory, "--force", expected=0)
        name = f"sim-{role}.enroll.json"
        target = destination / name
        prod.run("docker", "cp", f"{self.ids[role]}:{directory}/{name}", target)
        bundle = json.loads(target.read_text(encoding="utf-8-sig"))
        assert set(bundle) == {"schemaVersion", "clientId", "keyId", "hostName", "ipv4Addresses", "createdAtUtc", "publicKeyPem"}
        assert self.ips[role] in bundle["ipv4Addresses"] and bundle["publicKeyPem"].startswith("-----BEGIN PUBLIC KEY-----")

    def enrollment(self, label, suffix="", dry=False, expected=0):
        volumes = [(self.fx.project + "_store", "/dt/store", "rw"),
                   (self.fx.project + "_config", "/dt/config", "rw"),
                   (self.fx.project + "_keys", KEYS, "ro")]
        self.fx.guard_docker()
        for name, _, _ in volumes:
            self.fx.require_volume(name)
        area = self.work / ("admin" + suffix)
        cmd = ["docker", "run", "--rm", "--network", "none", *self.fx.docker_labels(),
               "--user", "0:20001", "--entrypoint", "bash"]
        for name, mount, mode in volumes:
            cmd += ["-v", f"{name}:{mount}:{mode}"]
        for source, destination in ((area / "bundles", BUNDLES),
                                    (area / "addresses.csv", "/dt/config/enroll-addresses.csv")):
            cmd += ["--mount", f"type=bind,source={source},target={destination},readonly"]
        args = [SERVER, "client-enroll", "--from", BUNDLES, "--grant", "demo:prod:stable",
                "--address-source", "file:/dt/config/enroll-addresses.csv", "--config", "/dt/config/server.json"]
        if dry:
            args.append("--dry-run")
        return self.command(label, [*cmd, self.fx.image, "-ec", 'umask 0027; exec "$@"', "_", *args], expected)

    def snapshot(self, label):
        inventory = self.fx.dist("bash", "-e", "-o", "pipefail", "-c",
                                 'cd /dt/store; find . -printf "%y %m %u:%g %p\\n" | LC_ALL=C sort; '
                                 'find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum')
        policy_hash = self.fx.dist("sha256sum", POLICY).split()[0]
        keys = json.loads(self.fx.server("client-key", "list"))
        audit = int(self.fx.dist("bash", "-ec", "wc -l </dt/store/authority/audit.jsonl"))
        head = json.loads(self.fx.dist("cat", "/dt/store/authority/HEAD"))
        (self.evidence / f"{label}-tree.txt").write_text(inventory + "\n", encoding="utf-8")
        data = {"treeSha256": hashlib.sha256(inventory.encode()).hexdigest(), "policySha256": policy_hash,
                "keys": keys, "auditEntries": audit, "generation": head["generation"]}
        self.save(f"{label}.json", data)
        return data

    def stat_policy(self, label):
        return {"file": self.fx.dist("stat", "-c", "%U:%G %a", POLICY),
                "directory": self.fx.dist("stat", "-c", "%U:%G %a", "/dt/config"),
                "accessAcl": self.fx.dist("getfacl", "-c", "-n", "--", POLICY).splitlines(),
                "tmp": self.fx.dist("find", "/dt/config", "-maxdepth", "1", "-name", ".client-enrollment-*"),
                "serveReadExit": self.command(label,
                    ("docker", "exec", self.compose("ps", "-q", "dist"), "setpriv",
                     "--reuid=uedt-distribution", "--regid=uedt-upload", "--groups=20001",
                     "test", "-r", POLICY))[0]}

    def verify_policy(self, before, after):
        named_entry_present = all(POLICY_ACL_ENTRY in snapshot["accessAcl"] for snapshot in (before, after))
        passed = before == after and after["serveReadExit"] == 0 and not after["tmp"] and named_entry_present
        self.gate(5, "PASS" if passed else "FAIL", before=before, after=after,
                  namedAclPresent=named_entry_present, accessAclEqual=before["accessAcl"] == after["accessAcl"])
        if not passed:
            raise RuntimeError("G5 failed: inspect the before/after policy metadata and ACL")

    def area(self, suffix, rows):
        area = self.work / ("admin" + suffix)
        bundles = area / "bundles"
        bundles.mkdir(parents=True, exist_ok=True)
        (area / "addresses.csv").write_text(
            "clientId,address\n" + "".join(f"sim-{r},{ip}\n" for r, ip in rows.items()), encoding="utf-8")
        return bundles

    def nginx(self):
        return self.fx.compose("logs", "--no-color", "--no-log-prefix", "nginx").splitlines()

    def observations(self, cursor):
        return [dict(zip(("ip", "status", "port", "path"), row.split()[:4]))
                for row in self.nginx()[cursor:] if len(row.split()) >= 4
                and row.split()[2] == "18080" and row.split()[3].startswith(("/api/", "/releases/"))]

    def finish(self, role, suffix, expected):
        config = f"{H}/{suffix}.json"
        self.sim(role, f"sim-{role}-finish-{suffix}", prod.LINUX_CLI, "enroll", "finish", "--storage", "portable",
                 "--server-url", prod.ORIGIN, "--project-id", "demo", "--platform", "linux-x64",
                 "--credential-name", f"device-{suffix}", "--signing-key-id", "prod-01d-release",
                 "--public-key", H + "/release-public.pem", "--output", config, expected=expected)
        return config

    def doctor(self, role, label, expected, config, ip):
        cursor = len(self.nginx())
        doctor_exit, result = self.sim(role, label, prod.LINUX_CLI, "doctor", "--config", config,
                                       "--online", "--format", "json")
        doctor = json.loads(result)
        checks = {x["name"]: x for x in doctor["checks"]}
        requests = self.observations(cursor)
        catalog = [int(x["status"]) for x in requests if x["ip"] == ip and x["path"] == "/api/v1/catalog"]
        evidence = {"doctorExit": doctor_exit, "expectedHttp": expected, "catalogHttp": catalog,
                    "catalogVerifiedByDoctor": checks["catalog-online"]["success"],
                    "releaseCheck": checks.get("authorized-releases", {}).get("code"),
                    "observed": requests}
        self.save(label + "-http.json", evidence)
        assert catalog == [expected], evidence
        assert evidence["catalogVerifiedByDoctor"] is (expected == 200), evidence
        return evidence

    def count_catalog(self, role, cfg, count):
        _, value = self.sim(role, f"sim-{role}-signed-catalog", H + "/catalog-proof", cfg, expected=0)
        proof = json.loads(value)
        assert proof["authenticated"] and proof["count"] == count, proof
        if count:
            assert proof["releases"] == [{"project": "demo", "Version": "1.0.0", "Platform": "linux-x64",
                                          "Environment": "prod", "Channel": "stable"}]
        return proof

    def install(self, role, config):
        cursor = len(self.nginx())
        self.sim(role, f"sim-{role}-install", prod.LINUX_CLI, "run", "--no-launch", "--config", config, expected=0)
        requests = self.observations(cursor)
        track = "demo/prod/stable/1.0.0/linux-x64"
        _, raw = self.sim(role, f"sim-{role}-installed-manifest", "cat", f"{H}/.state/{track}/installed-manifest.json", expected=0)
        manifest = json.loads(raw)
        assert manifest["version"] == "1.0.0" and manifest["platform"] == "linux-x64"
        assert {item["path"] for item in manifest["files"]} == {"game.sh", "version.txt"}
        for item in manifest["files"]:
            target = f"{H}/app/{track}/{item['path']}"
            _, facts = self.sim(role, f"sim-{role}-payload-{item['path']}", "bash", "-ec",
                                'stat -c %s "$1"; sha256sum "$1"', "_", target, expected=0)
            size, digest = facts.split()[:2]
            assert int(size) == item["size"] and digest.lower() == item["sha256"].lower()
        assert any(x["status"] == "200" and x["ip"] == self.ips[role] and x["path"].endswith("/manifest.json") for x in requests), requests
        return {"manifestAndPayloadFileCount": len(manifest["files"]), "manifestChecks": requests}

    def secret_scan(self, groups):
        # Search contents without ever printing a matching value. Exclude only
        # reproduction source, publication logs and non-evidence metadata from secret pattern self-matches.
        pattern = __import__("re").compile(rb"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----|\"privateKeyPem\"\s*:", __import__("re").I)
        paths = [p for group in groups for p in group.rglob("*") if p.is_file()]
        hits = [str(p) for p in paths if p.suffix.lower() in (".keycred", ".cred", ".pfx") or pattern.search(p.read_bytes())]
        assert not hits, "Private key material detected in exported evidence"
        return len(paths)

    def verify(self):
        self.attach()
        self.gate(1, "N/A", reason="No repository changes")
        if not (self.evidence / "publish-demo.json").exists():
            self.publish()
        rows = {role: (self.ips[role] if role != "d" else self.moved) for role in "abcd"}
        bundles = self.area("", rows)
        excluded = self.work / "excluded-e"
        excluded.mkdir(exist_ok=True)
        for role in ROLES:
            self.prepare(role, "v1", bundles if role != "e" else excluded)
        self.gate(2, "PASS", filesScanned=self.secret_scan((bundles, excluded, self.logs, self.evidence)))
        before_policy = self.stat_policy("serve-read-before")
        if POLICY_ACL_ENTRY not in before_policy["accessAcl"]:
            self.gate(5, "FAIL", before=before_policy, reason="Expected named ACL missing before enrollment")
            raise RuntimeError("G5 failed: the extended fixture ACL is missing")
        before = self.snapshot("dry-before")
        _, dry = self.enrollment("client-enroll-dry-run", dry=True)
        plan = json.loads(dry)
        after = self.snapshot("dry-after")
        assert plan["dryRun"] and plan["keysToAdd"] == 4 and plan["policiesToAdd"] == 3 and before == after
        self.gate(3, "PASS", keysToAdd=4, policiesToAdd=3, treeSha256=after["treeSha256"], generation=after["generation"])

        _, applied = self.enrollment("client-enroll-apply")
        applied = json.loads(applied)
        assert not applied["dryRun"] and applied["keysToAdd"] == 4 and applied["policiesToAdd"] == 3
        after_policy = self.stat_policy("serve-read-after")
        self.verify_policy(before_policy, after_policy)
        registered = self.snapshot("apply-after")
        assert len(registered["keys"]) == 4 and registered["auditEntries"] - after["auditEntries"] == 4

        bad = self.area("-invalid", {"a": "not-an-ip", "e": self.ips["e"]})
        shutil.copyfile(bundles / "sim-a.enroll.json", bad / "sim-a.enroll.json")
        shutil.copyfile(excluded / "sim-e.enroll.json", bad / "sim-e.enroll.json")
        before_bad = self.snapshot("n1-before")
        self.enrollment("n1-invalid-csv", "-invalid", expected=1)
        after_bad = self.snapshot("n1-after")
        assert before_bad == after_bad
        self.gate(7, "PASS", exit=1, treeSha256=after_bad["treeSha256"], generation=after_bad["generation"])

        before_same = self.snapshot("n2-before")
        _, same = self.enrollment("n2-same-batch")
        same = json.loads(same)
        after_same = self.snapshot("n2-after")
        assert same["keysToAdd"] == 0 and same["policiesToAdd"] == 0 and before_same == after_same
        self.gate(8, "PASS", keysAdded=0, auditAdded=0, generation=after_same["generation"])

        proof = self.priv / "catalog-proof-publish/UeDtLauncher.Tests"
        for role in "abc":
            prod.run("docker", "cp", proof, f"{self.ids[role]}:{H}/catalog-proof")
            prod.run("docker", "exec", "--user", "0", self.ids[role], "chmod", "0755", H + "/catalog-proof")
        cases, configs, installations = {}, {}, {}
        for role, status in zip(ROLES, (200, 200, 200, 403, 401)):
            config = self.finish(role, "v1", int(status != 200))
            configs[role] = config
            cases[role] = self.doctor(role, f"sim-{role}-doctor-v1", status, config, self.ips[role])
            if role in "abc":
                cases[role]["signedCatalog"] = self.count_catalog(role, config, int(role != "c"))
            if role in "ab":
                installations[role] = self.install(role, config)
        self.gate(4, "PASS", cases=cases)
        self.gate(6, "PASS", installations=installations)

        result = self.fx.server("client-key", "revoke", "--key-id", "sim-a-v1")
        assert "revoked" in result
        revoked = self.doctor("a", "n3-revoked", 401, configs["a"], self.ips["a"])
        new = self.area("-new-key", {"a": self.ips["a"]})
        self.prepare("a", "v2", new)
        _, recovery = self.enrollment("n3-reenroll-new-key", "-new-key")
        assert json.loads(recovery)["keysToAdd"] == 1
        config = self.finish("a", "v2", 0)
        recovered = self.doctor("a", "n3-recovered", 200, config, self.ips["a"])
        old_still_revoked = self.doctor("a", "n3-old-still-revoked", 401, configs["a"], self.ips["a"])
        self.gate(9, "PASS", revoked=revoked, recovered=recovered, oldKey=old_still_revoked)

        self.gate(10, "NOT_RUN", reason="No management command changes an existing client's policy address")
        self.fx.guard_docker()
        prod.run("docker", "network", "disconnect", self.network, self.ids["a"])
        try:
            prod.run("docker", "network", "connect", "--ip", self.moved, self.network, self.ids["a"])
            try:
                moved = self.doctor("a", "n4-supplement-moved", 403, config, self.moved)
            finally:
                prod.run("docker", "network", "disconnect", self.network, self.ids["a"])
        finally:
            prod.run("docker", "network", "connect", "--ip", self.ips["a"], self.network, self.ids["a"])
        restored = self.doctor("a", "n4-supplement-restored", 200, config, self.ips["a"])
        self.gate(10, "NOT_RUN", reason="No management command changes an existing client's policy address",
                  supplemental={"moved": moved, "restored": restored})
        self.save("nginx-fixture-requests.json", self.observations(0))
        self.gate(2, "PASS", filesScanned=self.secret_scan((bundles, excluded, bad, new, self.logs, self.evidence)))

    def cleanup(self):
        try:
            if self.fx.ownership:
                self.fx.guard_docker()
                if self.overlay.exists():
                    self.compose("rm", "-f", "-s", *(f"sim-{r}" for r in ROLES))
                self.fx.down()
                shutil.copyfile(self.work / "docker-residual.json", self.evidence / "docker-residual.json")
        finally:
            after = self.inventory("after")
            path = self.evidence / "docker-before.json"
            if path.exists():
                before = json.loads(path.read_text())
                diff = {k: {"added": sorted(set(after[k]) - set(before[k])),
                             "removed": sorted(set(before[k]) - set(after[k]))} for k in after}
                self.save("docker-inventory-delta.json", diff)
                residual = self.evidence / "docker-residual.json"
                clean = not any(any(v.values()) for v in diff.values())
                residual_clean = residual.exists() and not any(json.loads(residual.read_text()).values())
                self.gate(11, "PASS" if clean and residual_clean else "FAIL", diff=diff, ownedResidualZero=residual_clean)
            else:
                self.gate(11, "FAIL", reason="Missing before-run Docker inventory")
        if self.gates["G11"]["status"] == "FAIL":
            raise RuntimeError("G11 failed: inspect the Docker inventory and ownership evidence")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("start", "verify", "cleanup", "create_bundle_mountpoint"))
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--private", required=True, type=Path)
    parser.add_argument("--run-id", required=True)
    args = parser.parse_args()
    getattr(Run(args), args.operation)()


if __name__ == "__main__":
    main()
