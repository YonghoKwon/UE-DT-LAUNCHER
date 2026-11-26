# PROD-02D local download measurements

Use a dedicated Windows Docker Desktop measurement window. Prepare the published
win-x64 and linux-x64 cohorts plus the Linux multi-file server with the [01D
publish commands](../README.md#준비와-기동). Use a fresh private work directory
containing those publications in `win`, `linux`, and `server-multi`. The tool
uses the existing staged m7at10 Windows package through a read-only mount.

```powershell
$bench = 'tools/docker/prod-server/bench'
$id = 'd02d' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$args02d = @('--run-id', $id, '--work', $privateRoot, '--evidence', $evidence,
             '--ue', 'C:\Unreal Projects\m7at10_dt\Windows')
python -B "$bench/matrix.py" prepare @args02d
python -B "$bench/matrix.py" measure @args02d
python -B "$bench/summarize.py" $evidence $summary
```

Use existing empty evidence and fresh private publication directories.
`measure` removes the owned Docker resources after the matrix finishes.
If execution stops partway through and the fixture is no longer needed, use
`matrix.py down @args02d` after recording the error.
The benchmark ownership records remain in the private directory. The result
folder contains no credential or signing key. Remove the private work directory
after verifying that the intended Docker resources are gone.

The ten signed simulators use the unchanged 02T driver and separate in-container
keys. Each slot has an explicitly assigned IP in a Docker IPAM subnet and
resolves the signed origin to the private dist address. Docker Compose cannot
assign a different static address to each scaled replica, so the runner starts
the simulator slots directly on the fixture network. Only synthetic bytes are
visible on the temporary private M0 path. The Windows-published HTTP endpoint
returns 404 even during M0. The runner recreates nginx without that mount and
probes both routes for 404 before a signed round.

The aggregate transfer rate divides received file bytes by the elapsed window
from the first file GET to the last participant's final transfer outcome.
Attempt latency includes unsuccessful requests and the intentional M1
disconnect. File outcome latency includes successful and failed participants
that produced reports. A missing slot is a final failure with an unknown
attempt count and latency. Throughput comparisons use all three matched rounds.
Docker Desktop's OS page cache is under normal host control. M3 starts with an
empty product client installation and cache.

M4 stops the directly owned product CLI after its private partial file reaches
the byte threshold. It checks the retained length after process exit against
the next request's Range and the 206 Content-Range plus sent bytes, and verifies
the installed file against Manifest SHA-256. The small-budget round tracks the
normal staging scratch instead of looking for an over-budget resume entry.
