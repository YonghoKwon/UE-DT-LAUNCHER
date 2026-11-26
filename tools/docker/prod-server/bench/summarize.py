"""Aggregate fixed-participant PROD-02D reports without dropping failed attempts."""

import argparse
import json
import math
from pathlib import Path
import statistics


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def percentile(values, fraction):
    return sorted(values)[math.ceil(len(values) * fraction) - 1] if values else None


def environment(folder, payload_bytes):
    path = folder / "resources.jsonl"
    samples = [json.loads(line) for line in path.read_text().splitlines()]
    if len(samples) < 2 or any(not row["backendKiB"] or "oom_kill" not in row["vmCounters"] for row in samples):
        raise ValueError(f"Missing VM or backend samples in {path.name}")
    vm_cpu, backend_cpu = [], []
    for previous, current in zip(samples, samples[1:]):
        ticks = [end - start for start, end in zip(previous["cpuTicks"], current["cpuTicks"])]
        total = sum(ticks)
        if total > 0:
            vm_cpu.append(100 * (total - ticks[3] - ticks[4]) / total)
            backend_cpu.append(100 * (current["backendCpuTicks"] - previous["backendCpuTicks"]) / total)
    if not vm_cpu:
        raise ValueError("No positive CPU sampling interval")
    first, last = samples[0], samples[-1]
    disk_minimum = min(row["vmDiskAvailableBytes"] for row in samples)
    limits = {
        "cpuAtLeast90Percent": max(vm_cpu) >= 90,
        "swapIncreased": any(row["vmKiB"]["SwapFree"] < first["vmKiB"]["SwapFree"]
                             or row["vmCounters"]["pswpout"] > first["vmCounters"]["pswpout"] for row in samples),
        "oomKillIncreased": last["vmCounters"]["oom_kill"] > first["vmCounters"]["oom_kill"],
        "vmDiskBelowThreshold": disk_minimum < payload_bytes * 2,
    }
    rss = max(row["backendKiB"]["VmRSS"] for row in samples)
    high_water = max(row["backendKiB"]["VmHWM"] for row in samples)
    if high_water > first["backendKiB"]["VmHWM"]:
        rss = max(rss, high_water)
    return {"limited": any(limits.values()), "limits": limits, "cpuPeakPercent": max(vm_cpu),
            "backendCpuPeakPercentOfVm": max(backend_cpu),
            "backendRssPeakBytes": rss * 1024, "backendLifetimeHwmBytes": high_water * 1024,
            "sampleCount": len(samples), "vmDiskMinimumBytes": disk_minimum, "before": first, "after": last}


def transfer_metrics(folder, group, participants):
    reports, errors = [], []
    for slot in range(1, participants + 1):
        path = folder / f"slot-{slot}.json"
        if not path.is_file():
            errors.append({"slot": slot, "error": "missing-report"})
            continue
        report = load(path)
        if report.get("barrier") != {"groupId": group, "participants": participants, "slot": slot}:
            errors.append({"slot": slot, "error": "mismatched-participant"})
            continue
        reports.append(report)
        if report["status"] != "ok":
            errors.append({"slot": slot, "error": report["error"]})
    file_seconds, rates = [], []
    starts, ends = [], []
    requests = [row for report in reports for row in report["requests"]]
    file_attempts = [row for row in requests if row["kind"].startswith("file")]
    for report in reports:
        rows = report["requests"]
        default = (report["endUnixNs"] - report.get("transferStartUnixNs", report["startUnixNs"])) / 1e9
        seconds = report.get("transferElapsedSeconds", default)
        file_seconds.append(seconds)
        received = sum(row["bytes"] for row in rows if row["kind"].startswith("file"))
        rates.append(received / (seconds * 1e6) if seconds > 0 else 0)
        file_start = report.get("firstFileGetUnixNs")
        if file_start is not None:
            starts.append(file_start)
            ends.append(report.get("transferEndUnixNs", report["endUnixNs"]))
    window = (max(ends) - min(starts)) / 1e9 if starts else None
    wire_bytes = sum(row["bytes"] for row in file_attempts)
    attempt_seconds = [row["elapsedSeconds"] for row in requests]
    first_failures = sum(any(row["retry"] == 0 and row["error"] not in (None, "intentional-disconnect")
                             for row in report["requests"]) for report in reports)
    resumptions = []
    for report in reports:
        disconnected = [row for row in report["requests"] if row["error"] == "intentional-disconnect"]
        ranged = [row for row in report["requests"] if row["kind"] == "file-resume"]
        if len(disconnected) == len(ranged) == 1:
            resumed = ranged[0]
            resumptions.append({"offset": disconnected[0]["bytes"], "range": resumed["range"],
                                "status": resumed["status"], "contentRange": resumed.get("contentRange"),
                                "sha256": report.get("sha256"), "expectedSha256": report["expectedSha256"]})
    access_path = folder / "access.json"
    access = load(access_path) if access_path.is_file() else []
    paths = folder.parent.parent / "paths.json"
    peers = load(paths)["addresses"] if paths.is_file() else {}
    affected = {peers.get(f"sim-{error['slot']}") for error in errors if error["error"] in ("5xx", "timeout")}
    backend_failures = [item for item in access
                        if (item["port"] == 18080 and item["status"] >= 500
                            and item["peer"] in affected
                            and (item["upstreamHeaderSeconds"] != "-" or item["upstreamStatus"] == "504"))]
    return {
        "participants": participants, "reports": len(reports), "firstFailures": first_failures,
        "missingReports": participants - len(reports), "finalFailures": len(errors), "errors": errors,
        "attempts": len(requests), "fileAttempts": len(file_attempts), "wireBytes": wire_bytes,
        "firstGetSkewMs": (max(starts) - min(starts)) / 1e6 if starts else None,
        "fileSecondsP50": percentile(file_seconds, .5), "fileSecondsP95": percentile(file_seconds, .95),
        "attemptSecondsP50": percentile(attempt_seconds, .5), "attemptSecondsP95": percentile(attempt_seconds, .95),
        "minimumMBps": min(rates) if len(rates) == participants else None,
        "aggregateMBps": wire_bytes / (window * 1e6) if window and len(starts) == participants else None,
        "resumptions": resumptions, "backendFailures": backend_failures,
    }


def plan():
    for variant, payload, groups in (("M0", "20gb", (1,)), ("M0", "1gb", (1, 5, 10)),
                                      ("M1", "20gb", (1,)), ("M2", "1gb", (1, 5, 10))):
        for count in groups:
            for index in range(4):
                yield f"{variant}-{payload}-n{count}-{index}", count, index == 0
    yield "M3-cold", None, False
    for index in range(1, 4):
        yield f"M4-positive-{index}", None, False
    yield "M4-negative-1", None, False


def collect(evidence, missing_reason):
    result = {}
    for group, participants, warmup in plan():
        folder = evidence / "runs" / group
        environment_path = folder / "environment.json"
        if not folder.is_dir():
            prefix = group.rsplit("-", 2)[0]
            stop = evidence / f"{prefix}-escalation-stop.json"
            reason = (f"{prefix}: environment limited at concurrency {load(stop)['limitedAt']}"
                      if stop.is_file() else missing_reason)
            result[group] = {"status": "NOT_RUN", "reason": reason, "warmup": warmup}
            continue
        product = folder / "product.json"
        metrics = (transfer_metrics(folder, group, participants) if participants else
                   load(product) if product.is_file() else None)
        result[group] = {"status": "RECORDED" if metrics else "INCOMPLETE", "warmup": warmup,
                         "metrics": metrics, "environment": load(environment_path) if environment_path.is_file() else None}
    return result


def eligible(records):
    return all(row["status"] == "RECORDED" and row["environment"] is not None
               and not row["environment"]["limited"] for row in records)


def resume_pass(row):
    product = row.get("metrics") or {}
    before = product.get("initial") or {}
    after = product.get("repeat") or {}
    if not before.get("interrupted") or after.get("exit") != 0 or product.get("verifiedBytes", 0) < 20_000_000_000:
        return False
    offset = before["retainedBytes"]
    files = [item for item in product["repeatHttp"] if item["path"].endswith("/payload.bin")]
    if len(files) != 1:
        return False
    transferred = [item["bodyBytes"] for item in product["repeatHttp"] if "/files/" in item["path"]]
    if (after.get("performance") or {}).get("networkBytes") != sum(transferred):
        return False
    item = files[0]
    if product["resumeBudgetBytes"] < 20_000_000_000:
        return item["status"] == 200 and item["range"] == "-" and item["bodyBytes"] == 20_000_000_000
    expected = f"bytes {offset}-19999999999/20000000000"
    return (item["status"] == 206 and item["range"] == f"bytes={offset}-" and item["contentRange"] == expected
            and item["bodyBytes"] == 20_000_000_000 - offset)


def measured_records(rows, prefix):
    return [rows[f"{prefix}-{index}"] for index in range(1, 4)]


def comparison(rows, signed, baseline):
    signed_rows, baseline_rows = measured_records(rows, signed), measured_records(rows, baseline)
    if any((row.get("environment") or {}).get("limited") for row in signed_rows + baseline_rows):
        return {"status": "ENVIRONMENT_LIMITED"}
    if not eligible(signed_rows + baseline_rows):
        return {"status": "UNDETERMINED"}
    signed_rates = [row["metrics"]["aggregateMBps"] for row in signed_rows]
    baseline_rates = [row["metrics"]["aggregateMBps"] for row in baseline_rows]
    if any(value is None for value in signed_rates + baseline_rates) or any(value <= 0 for value in baseline_rates):
        return {"status": "UNDETERMINED"}
    ratios = [s / b for s, b in zip(signed_rates, baseline_rates)]
    return {"status": "TRIGGERED" if min(ratios) < .5 else "NOT_TRIGGERED",
            "ratios": ratios, "medianRatio": statistics.median(signed_rates) / statistics.median(baseline_rates)}


def verdicts(rows):
    m1 = measured_records(rows, "M1-20gb-n1")
    l3_proofs = []
    for row in m1:
        resumption = ((row.get("metrics") or {}).get("resumptions") or [{}])[0]
        offset = resumption.get("offset")
        l3_proofs.append(bool(offset and resumption["status"] == 206
                              and resumption["contentRange"] == {"start": offset, "end": 19_999_999_999,
                                                                 "total": 20_000_000_000}
                              and resumption["sha256"] == resumption["expectedSha256"]
                              and row["metrics"]["finalFailures"] == 0))
    m4 = measured_records(rows, "M4-positive") + [rows["M4-negative-1"]]
    m2 = {n: measured_records(rows, f"M2-1gb-n{n}") for n in (1, 5, 10)}
    verified = [n for n, records in m2.items() if eligible(records)
                and all(row["metrics"]["finalFailures"] == 0 for row in records)]
    ten = m2[10]
    ten_errors = [item for row in ten if row.get("metrics") for item in row["metrics"]["errors"]]
    backend_failures = [item for row in ten if row.get("metrics") for item in row["metrics"]["backendFailures"]]
    t1 = "ENVIRONMENT_LIMITED" if any((row.get("environment") or {}).get("limited") for row in ten) else "UNDETERMINED"
    if eligible(ten) and not ten_errors:
        t1 = "NOT_TRIGGERED"
    elif eligible(ten) and not any(item["error"] in ("5xx", "timeout") for item in ten_errors):
        t1 = "NOT_TRIGGERED"
    elif eligible(ten) and backend_failures:
        t1 = "TRIGGERED"
    # A driver 5xx/timeout alone does not attribute the failure to the backend.
    samples = [row["environment"]["backendRssPeakBytes"] for row in rows.values() if eligible([row])]
    rss = max(samples, default=None)
    t3 = ("TRIGGERED" if rss is not None and rss > 2_147_483_648 else
          "NOT_TRIGGERED" if eligible(list(rows.values())) else "UNDETERMINED")
    return {
        "L3": {"verdict": "PASS" if all(l3_proofs) else "FAIL" if eligible(m1) else "UNDETERMINED",
               "exactRangeAndSha256": l3_proofs},
        "L4": {"verdict": "PASS" if all(map(resume_pass, m4)) else "FAIL" if eligible(m4) else "UNDETERMINED",
               "roundProofs": list(map(resume_pass, m4))},
        "L5": {"verdict": "PASS" if any(n >= 5 for n in verified) else "FAIL" if eligible(m2[5]) else "UNDETERMINED",
               "verifiedConcurrency": verified, "maxVerified": max(verified, default=None)},
        "T13-1": {"status": t1, "errors": ten_errors, "backendFailures": backend_failures},
        "T13-2": comparison(rows, "M2-1gb-n10", "M0-1gb-n10"),
        "T13-3": {"status": t3, "peakRssBytes": rss},
        "T13-4": comparison(rows, "M1-20gb-n1", "M0-20gb-n1"),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--missing-reason", default="See progress and preflight evidence")
    args = parser.parse_args()
    rows = collect(args.evidence, args.missing_reason)
    args.output.write_text(json.dumps({"scope": "local", "rounds": rows, "verdicts": verdicts(rows)},
                                      indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
