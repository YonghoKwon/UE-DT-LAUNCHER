"""Run capability proof with a published launcher, never with company applications."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True)
    parser.add_argument("--root")
    args = parser.parse_args()
    root = Path(args.root or tempfile.mkdtemp(prefix="uedt-runtime-proof-")).resolve()
    root.mkdir(parents=True, exist_ok=True)
    launcher = str(Path(args.launcher).resolve())
    fixture = str(Path(__file__).with_name("runtime-family-fixture.py").resolve())
    results = []
    cases = ["direct", "root-exits", "host-crash"] + ([] if os.name == "nt" else ["double-fork"])
    for mode in cases:
        directory = root / mode
        directory.mkdir()
        request = {"executable": sys.executable, "workingDirectory": str(directory),
                   "arguments": [fixture, "root-exits" if mode == "host-crash" else mode, str(directory)]}
        started = time.monotonic()
        child = subprocess.Popen([launcher, "runtime-host", "--capability-probe"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                 text=True, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        try:
            child.stdin.write(json.dumps(request) + "\n"); child.stdin.close(); child.stdin = None
            deadline = time.monotonic() + 15
            while not (directory / "child-started").exists() and time.monotonic() < deadline and child.poll() is None:
                time.sleep(0.02)
            if not (directory / "child-started").exists():
                output, error = child.communicate(timeout=5)
                raise RuntimeError(f"Payload did not start ({mode}): {output} {error}")
            time.sleep(0.2)
            if child.poll() is not None:
                raise RuntimeError(f"Host exited before final descendant ({mode})")
            if mode == "host-crash":
                child.kill()  # only this newly created host; never name-based process cleanup
            output, error = child.communicate(timeout=15)
            while not (directory / "child-completed").exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            if not (directory / "child-completed").exists():
                raise RuntimeError(f"Descendant was lost or killed ({mode})")
            if mode != "host-crash":
                events = [json.loads(line) for line in output.splitlines() if line.strip()]
                if child.returncode != 0 or not events or events[-1]["state"] != "completed":
                    raise RuntimeError(f"Native quiescence was not proven ({mode}): {error}")
                result = events[-1]["result"]
                minimum = 3 if mode == "double-fork" else 2 if mode == "root-exits" else 1
                if result["ReapedProcesses"] < minimum:
                    raise RuntimeError(f"Missing descendants in native accounting ({mode})")
            results.append({"case": mode, "passed": True, "elapsedSeconds": round(time.monotonic() - started, 3)})
        finally:
            if child.poll() is None:
                child.kill(); child.wait(timeout=5)
    (root / "summary.json").write_text(json.dumps(results, indent=2))
    print("PASS: " + str(root))


if __name__ == "__main__":
    main()
