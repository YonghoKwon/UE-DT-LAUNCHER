"""PROD-08A noninteractive published transfer matrix. No desktop access."""
import argparse
import json
import os
from pathlib import Path
import runpy
import struct
import subprocess
import sys
import tempfile
import time
import uuid

args_parser = argparse.ArgumentParser(description=__doc__)
args_parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
args_parser.add_argument("--cohort", type=Path, required=True)
args_parser.add_argument("--output", type=Path, required=True, help="New result JSON path")
args = args_parser.parse_args()
if os.name != "nt":
    args_parser.error("This fixture uses Windows console Agent IPC.")
if args.output.exists() or not args.output.parent.is_dir():
    args_parser.error("--output requires a new JSON file in an existing directory.")
args.repo = args.repo.resolve(strict=True)
args.cohort = args.cohort.resolve(strict=True)
sys.path.insert(0, str(args.repo / "tools"))
import gui_fixture_evidence as evidence
from evidence_contract import atomic

publication = json.loads(args.cohort.read_text(encoding="utf-8"))
if publication.get("success") is not True or publication.get("rid") != "win-x64":
    args_parser.error("--cohort must be a successful win-x64 publication.")
head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=args.repo, text=True).strip()
if publication["sourceHead"] != head or subprocess.check_output(["git", "status", "--porcelain", "--", "src", "tools"], cwd=args.repo):
    args_parser.error("Publish the clean current source snapshot before running this fixture.")
payload_size = 64 * 1024 * 1024
proof = {
    "sourceHead": publication["sourceHead"],
    "productSourceHash": publication["productSourceHash"],
    "preferencesBefore": evidence.preference_hash(),
    "guiInput": False,
    "cases": [],
}

def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def bound_operation(response, phase, selection):
    operation = response.get("operation") or {}
    require(operation.get("phase") == phase,
            f"Expected {phase}, observed {operation.get('phase')} ({response.get('errorCode') or response.get('status')})")
    require(all(operation["selection"].get(key) == value for key, value in selection.items()),
            "Agent returned the operation for a different release tuple")
    require(len(operation.get("manifestSha256") or "") == 64, "Operation has no manifest digest")
    return operation


def wait_for(read, valid, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            value = read()
            if valid(value):
                return value
        except (FileNotFoundError, PermissionError, json.JSONDecodeError):
            pass
        time.sleep(.02)
    raise TimeoutError("Timed out waiting for fixture barrier")

def read_exact(pipe, size):
    data = bytearray()
    while len(data) < size:
        chunk = pipe.read(size - len(data))
        if not chunk:
            raise OSError("Incomplete response frame")
        data.extend(chunk)
    return data

def request(endpoint, command, on_progress=None, **fields):
    correlation = uuid.uuid4().hex
    data = json.dumps({
        "protocolVersion": 1, "command": command, "correlationId": correlation,
        "streamProgress": on_progress is not None,
        "clientCapabilities": ["runtime-supervision-v1", "cancellable-operations-v1"],
        **fields,
    }).encode()
    with open("\\\\.\\pipe\\" + endpoint, "r+b", buffering=0) as pipe:
        pipe.write(struct.pack("<i", len(data)) + data)
        while True:
            size = struct.unpack("<i", read_exact(pipe, 4))[0]
            if not 0 < size <= 1024 * 1024:
                raise ValueError("Invalid response frame")
            response = json.loads(read_exact(pipe, size))
            if response["correlationId"] != correlation:
                raise ValueError("Wrong correlation ID")
            if response.get("isFinal", True):
                return response
            for progress in response["progress"]:
                on_progress(correlation, progress)

def run_case(root, fixture, env, server, agent, _server_log, agent_log):
    case = proof["cases"][-1]
    require(fixture["sourceHead"] == proof["sourceHead"] and not fixture["sourceDirty"], "Fixture source differs from its publication")
    evidence.verify_cohort(root)
    # --prepare-gui deliberately leaves the 64 MiB v2 job pending.
    subprocess.run([sys.executable, str(args.repo / "tools" / "gui-fixture-control.py"),
                    "--root", str(root), "approve-v2"], check=True, timeout=120, capture_output=True)
    approval = json.loads(evidence.inside(root, "control/approval-status.json").read_text(encoding="utf-8"))
    require(approval.get("success") is True and approval.get("phase") == "complete",
            "The v2 fixture did not finish approval and promotion")
    case["fixtureV2ApprovedAndPromoted"] = True
    case["binarySha256"] = {kind: binary["sha256"] for kind, binary in fixture["binaries"].items()}
    endpoint = env["UE_DT_AGENT_ENDPOINT"]
    case["endpoint"] = endpoint
    config_path = evidence.inside(root, "client/agent/config/launcher.config.json")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    config["performance"]["resumeCacheBytes"] = case["budgetBytes"]
    config["performance"]["downloadConcurrency"] = 1
    config_path.write_text(json.dumps(config), encoding="utf-8")
    control = evidence.inside(root, "control")

    def metrics():
        return json.loads((control / "proxy-metrics.json").read_text(encoding="utf-8"))

    selection = {"projectId": "demo", "environment": "prod", "channel": "stable", "version": "2.0.0", "platform": fixture["platform"]}
    case["originalProcessIds"] = {"server": server.pid, "agent": agent.pid}
    unthrottle = control / "proxy-unthrottle"
    restarted = None

    def cancel_on_payload(correlation, progress):
        if progress["stage"] != "DownloadProgress" or progress.get("bytesDownloaded", 0) < 512 * 1024 or "atCancel" in case:
            return
        before = metrics()
        if before["throttledBytes"] < 512 * 1024 or not before["activeFiles"]:
            return
        cancel = request(endpoint, "operation-cancel", operationId=correlation)
        require(cancel.get("success") and (cancel.get("operation") or {}).get("cancellationRequested"),
                "The Agent did not acknowledge cancellation at the payload byte barrier")
        case["atCancel"] = {
            "phase": cancel["operation"]["phase"], "logicalBytes": progress["bytesDownloaded"],
            "pauseAvailable": progress.get("pauseAvailable"), "proxyPayloadBytes": before["throttledBytes"],
        }

    try:
        if case["id"] == "commit":
            unthrottle.touch()
            stages = []
            final = request(endpoint, "update", lambda _, p: stages.append(p["stage"]), projectId="demo", selection=selection)
            completed = bound_operation(final, "Completed", selection)
            require("Apply" in stages and final.get("installationCommitted") is True, "Commit was not observed")
            cancel = request(endpoint, "operation-cancel", operationId=completed["id"])
            require(cancel.get("success") and bound_operation(cancel, "Completed", selection) == completed,
                    "A late cancel changed the completed operation")
            case.update(observedApply=True, completedAfterLateCancel=True, installCommitted=final["installationCommitted"],
                        proxyPayloadBytes=wait_for(metrics, lambda x: x["activeFiles"] == 0)["throttledBytes"])
            return

        stopped = request(endpoint, "update", cancel_on_payload, projectId="demo", selection=selection)
        before = wait_for(metrics, lambda x: x["activeFiles"] == 0)
        require("atCancel" in case, "The payload byte/cancellation barrier was not observed")
        operation = bound_operation(stopped, "Cancelled", selection)
        positive = case["id"] in ("sufficient", "agent-restart")
        require(case["atCancel"]["pauseAvailable"] is positive, "Unexpected pause signal for this budget")
        require(stopped.get("retainedPartialDownload") is positive, "Unexpected retained partial signal")
        case.update(cancelledPhase=operation["phase"], retainedPartialDownload=stopped["retainedPartialDownload"],
                    payloadBytesBeforeResume=before["throttledBytes"], manifestSha256=operation["manifestSha256"])

        if case["id"] == "agent-restart":
            agent.terminate()
            agent.wait(timeout=10)
            restarted = subprocess.Popen([fixture["binaries"]["agent"]["path"]], env=env, stdout=agent_log,
                                         stderr=agent_log, creationflags=subprocess.CREATE_NO_WINDOW)
            evidence.wait_agent_ready(restarted, endpoint)
            persisted = request(endpoint, "operation-status", operationId=operation["id"])
            require(bound_operation(persisted, "Cancelled", selection) == operation, "Restart changed the retained operation")
            case.update(restartedAgentPid=restarted.pid, previousAgentExit=agent.returncode, statusMatchedAfterRestart=True)

        unthrottle.touch()
        final = request(endpoint, "operation-resume", lambda *_: None, projectId="demo", selection=selection, operationId=operation["id"])
        after = wait_for(metrics, lambda x: x["activeFiles"] == 0)
        resumed = after["throttledBytes"] - before["throttledBytes"]
        starts = after["rangeStarts"][len(before["rangeStarts"]):]
        case.update(resumePayloadBytes=resumed, rangeStarts=starts, completedPhase=(final.get("operation") or {}).get("phase"))
        completed = bound_operation(final, "Completed", selection)
        require(final.get("success") and final.get("installationCommitted"), "Resumed installation did not commit")
        require(completed["manifestSha256"] == operation["manifestSha256"], "Resume committed with a different manifest digest")
        if positive:
            require(len(starts) == 1 and starts[0] >= 1 and resumed == payload_size - starts[0],
                    "Expected one nonzero Range and exactly the remaining payload bytes")
        else:
            require(not starts and resumed == payload_size, "Expected full payload bytes without a Range")
    finally:
        if restarted is not None:
            if restarted.poll() is None:
                restarted.terminate()
                restarted.wait(timeout=10)
            case["restartedAgentExit"] = restarted.returncode

for name, budget in [
    ("sufficient", 8_000_000_000),
    ("undersized", 1_048_576),
    ("disabled", None),
    ("commit", 8_000_000_000),
    ("agent-restart", 8_000_000_000),
]:
    root = Path(tempfile.gettempdir()) / ("uedt-prod08a-" + uuid.uuid4().hex)
    case = {"id": name, "budgetBytes": budget, "runId": root.name, "status": "FAIL"}
    proof["cases"].append(case)
    saved_hold = evidence.hold_fixture
    evidence.hold_fixture = run_case
    try:
        sys.argv = [
            "test-intranet-auth.py", "--root", str(root), "--cohort", str(args.cohort),
            "--launcher", publication["binaries"]["launcher"]["path"],
            "--server", publication["binaries"]["server"]["path"],
            "--agent", publication["binaries"]["agent"]["path"],
            "--prepare-gui", publication["fixtureAssets"]["synthetic"]["path"],
            "--gui-download-proof",
        ]
        runpy.run_path(str(args.repo / "tools" / "test-intranet-auth.py"), run_name="__main__")
        case.update(status="PASS", ownedHandlesOnly=True)
    except Exception as error:
        case["failure"] = type(error).__name__ + ": " + str(error)
    finally:
        evidence.hold_fixture = saved_hold
        proof["preferencesAfter"] = evidence.preference_hash()
        atomic(args.output, proof)
    print(f"{name}: {case['status']}", flush=True)
sys.exit(any(case["status"] != "PASS" for case in proof["cases"]))
