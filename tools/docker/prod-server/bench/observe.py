"""Sample the isolated Docker VM and the published backend once per second."""

import json
import os
import sys
import time
from pathlib import Path


def proc_fields(path, names):
    rows = (line.split() for line in Path(path).read_text().splitlines())
    return {fields[0].rstrip(":"): int(fields[1])
            for fields in rows if fields and fields[0].rstrip(":") in names}


def sample(pid):
    cpu = Path("/proc/stat").read_text().splitlines()[0].split()[1:9]
    disk = os.statvfs("/")
    process = Path(f"/proc/{pid}")
    process_stat = (process / "stat").read_text().rsplit(")", 1)[1].split()
    return {
        "unixNs": time.time_ns(),
        "cpuTicks": [int(field) for field in cpu],
        "backendCpuTicks": int(process_stat[11]) + int(process_stat[12]),
        "backendKiB": proc_fields(process / "status", {"VmRSS", "VmHWM"}),
        "vmKiB": proc_fields("/proc/meminfo", {"MemTotal", "MemAvailable", "SwapTotal", "SwapFree"}),
        "vmCounters": proc_fields("/proc/vmstat", {"pswpin", "pswpout", "oom_kill"}),
        "vmDiskAvailableBytes": disk.f_bavail * disk.f_frsize,
    }


if __name__ == "__main__":
    stop = Path("/round/observe.stop")
    while True:
        print(json.dumps(sample(int(sys.argv[1]))), flush=True)
        if stop.exists():
            break
        time.sleep(1)
