# Reproduce PROD-06B locally

Run in PowerShell from the UE-DT-LAUNCHER repository root. The commands
publish the selected Git commit, using HEAD by default. Set the source SHA
explicitly to reproduce the historical cohort recorded in [RESULT.md](RESULT.md).
Use an exclusive Docker Desktop Linux window and the 01D port bindings. The script checks Windows DNS, the fixed TCP bindings,
running containers and run ownership before starting the fixture.

Choose fresh output paths. Keep the publication and disposable server fixture
in the private directory. Only public enrollment bundles are copied out of
simulators. Each simulator stores its credential in its own writable layer.

```powershell

$report = 'C:/path/to/fresh-report'
$scriptRoot = (Resolve-Path 'tools/docker/prod-server/enroll').Path
$private = Join-Path $env:LOCALAPPDATA ('uedt-06b-' + [Guid]::NewGuid().ToString('N').Substring(0,10))
$runId = 'd06b' + [Guid]::NewGuid().ToString('N').Substring(0,10)
New-Item -ItemType Directory -Path $private,$report | Out-Null
$sha = (git rev-parse HEAD).Trim()
foreach ($rid in 'linux-x64','win-x64') {
    python tools/publish_runtime_cohort.py --output "$private/$rid" --source-ref $sha --rid $rid
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
}
Copy-Item -LiteralPath "$scriptRoot/catalog-proof" -Destination $private -Recurse
$core = "$private/linux-x64/source/src/UeDtLauncher.Core/bin/Release/net8.0/UeDtLauncher.Core.dll"
dotnet publish "$private/catalog-proof/CatalogProof.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true "-p:FixtureCore=$core" -o "$private/catalog-proof-publish"
if ($LASTEXITCODE -ne 0) { throw 'Catalog proof publish failed' }

try {
    python "$scriptRoot/reproduce.py" start --report $report --private $private --run-id $runId
    if ($LASTEXITCODE -ne 0) { throw 'Startup failed' }
    python "$scriptRoot/reproduce.py" verify --report $report --private $private --run-id $runId
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
} finally {
    python "$scriptRoot/reproduce.py" cleanup --report $report --private $private --run-id $runId
    if ($LASTEXITCODE -ne 0) { throw 'Inspect Docker cleanup evidence' }
}
```

The separate catalog proof loads the same published source cohort's Core
assembly and emits counts and release tuples only. The fixture records actual
HTTP status from the existing nginx access log, in addition to running the
published launcher CLI. Retain the report and remove the disposable private
directory after reviewing the cleanup inventory.

The enrollment image adds the Rocky `acl` package in its private build context. Before serve starts, setup seeds a named read-only ACL on the synthetic policy. G5 compares numeric `getfacl -c -n` output and requires the named entry before and after enrollment. Cleanup writes the resource inventory comparison and exits nonzero on G11 FAIL.
