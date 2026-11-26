param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Private,
    [Parameter(Mandatory)][string]$UePackage,
    [string]$Output,
    [string]$SourceRef = 'HEAD'
)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $Repo
try {
    $Repo = (Get-Location).Path
    $sha = git rev-parse "$SourceRef^{commit}"
    if ($LASTEXITCODE -ne 0 -or $sha -ne (git rev-parse HEAD)) {
        throw 'The current checkout must match the chosen publication commit.'
    }
    $Private = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Private)
    if (-not $Output) { $Output = Join-Path $Repo 'artifacts/prod-04b-evidence' }
    if (Test-Path -LiteralPath $Private) { throw 'Use a new private publication directory.' }
    $win = Join-Path $Private 'win-x64'
    $linux = Join-Path $Private 'linux-x64'
    $multi = Join-Path $Private 'server-multi'
    python -B tools/publish_runtime_cohort.py --output $win --source-ref $sha --rid win-x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    python -B tools/publish_runtime_cohort.py --output $linux --source-ref $sha --rid linux-x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $server = Join-Path $linux 'source/src/UeDtLauncher.DistributionServer/UeDtLauncher.DistributionServer.csproj'
    dotnet publish $server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false "-p:SourceRevisionId=$sha" -o $multi
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    python -B (Join-Path $PSScriptRoot 'reproduce-04b.py') --repo $Repo --output $Output --private $Private --ue-package $UePackage --win-cohort (Join-Path $win 'cohort.json') --linux-cohort (Join-Path $linux 'cohort.json') --server-multi $multi --head $sha
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}