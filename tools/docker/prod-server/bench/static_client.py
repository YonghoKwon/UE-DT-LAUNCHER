"""Measure the M0 path using the same barrier, stream and hash logic as 02T."""

import argparse
import hashlib
import json
import time
from contextlib import nullcontext
from pathlib import Path

import driver


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--name", choices=("20gb", "1gb"), required=True)
    parser.add_argument("--size", type=int, required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--participants", type=int, required=True)
    parser.add_argument("--slot", type=int, required=True)
    parser.add_argument("--group", required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = {
        "status": "failed", "requests": [], "error": None,
        "expectedBytes": args.size, "expectedSha256": args.sha256,
        "startUnixNs": time.time_ns(), "retryCount": 0,
        "barrier": {"groupId": args.group, "participants": args.participants, "slot": args.slot},
    }
    client = driver.Client("http://host.docker.internal:18080", None, None, 120, report)
    with driver.report_file(args.report) as output:
        try:
            report["barrierStartUnixNs"] = driver.barrier(
                args.report.parent, args.group, args.participants, args.slot, 120)
            report["transferStartUnixNs"] = time.time_ns()
            start = time.monotonic()
            digest = hashlib.sha256()
            with client.request("file", f"/bench-static/{args.name}.bin", {}, 200) as (response, row):
                driver.validate_file_headers(response, row, 0, args.size - 1, args.size, False)
                driver.stream_bytes(response, row, args.size, digest, None)
                driver.require(not response.read(1), "unexpected-trailing-data")
            report["transferElapsedSeconds"] = time.monotonic() - start
            report["transferEndUnixNs"] = time.time_ns()
            report["sha256"] = digest.hexdigest()
            driver.require(report["sha256"] == args.sha256, "sha256-mismatch")
            report["status"] = "ok"
        except driver.REPORTABLE as error:
            report["error"] = driver.classify(error)
        finally:
            client.connection.close()
            report["endUnixNs"] = time.time_ns()
            report["requestCount"] = len(report["requests"])
            json.dump(report, output, indent=2)
    return int(report["status"] != "ok")


if __name__ == "__main__":
    raise SystemExit(main())
