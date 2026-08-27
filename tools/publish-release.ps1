param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$ServerRoot,
    [Parameter(Mandatory = $true)][string]$BaseUrlRoot,
    [Parameter(Mandatory = $true)][string]$ProjectId,
    [Parameter(Mandatory = $true)][string]$DisplayName,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet("prod", "dev")][string]$Environment,
    [Parameter(Mandatory = $true)][ValidateSet("stable", "beta", "dev")][string]$Channel,
    [Parameter(Mandatory = $true)][ValidateSet("windows-x64", "linux-x64")][string]$Platform,
    [Parameter(Mandatory = $true)][string]$EntryPoint,
    [Parameter(Mandatory = $true)][ValidateSet("general", "developer")][string]$CatalogProfile,
    [string[]]$AllowedClientProfiles,
    [string]$Notes = "",
    [string]$PrivateKeyPath = $env:UE_DT_SIGNING_PRIVATE_KEY,
    [string]$KeyId = $env:UE_DT_SIGNING_KEY_ID,
    [switch]$SetLatest,
    [switch]$DryRun,
    [switch]$Replace,
    [switch]$AllowUnsigned,
    [switch]$CleanFiles,
    [switch]$NoCopy,
    [string]$Remote
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
if ($NoCopy) { throw "-NoCopy is not supported by atomic publishing. Use update-catalog for catalog-only registration." }
if ($CleanFiles) { $Replace = $true }
if ($null -eq $AllowedClientProfiles -or $AllowedClientProfiles.Count -eq 0) { $AllowedClientProfiles = @($CatalogProfile) }

$launcher = $env:UE_DT_LAUNCHER_BIN
if ([string]::IsNullOrWhiteSpace($launcher)) {
    $candidate = Join-Path $repoRoot "publish/win-x64/UeDtLauncher.exe"
    if (Test-Path $candidate -PathType Leaf) { $launcher = $candidate }
}
if ([string]::IsNullOrWhiteSpace($launcher)) {
    $command = Get-Command UeDtLauncher -ErrorAction SilentlyContinue
    if ($null -ne $command) { $launcher = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($launcher) -or !(Test-Path $launcher -PathType Leaf)) {
    throw "UeDtLauncher executable was not found. Build it or set UE_DT_LAUNCHER_BIN."
}

$arguments = @(
    "publish-release", "--package-dir", $PackageDir, "--server-root", $ServerRoot,
    "--base-url-root", $BaseUrlRoot, "--project-id", $ProjectId, "--display-name", $DisplayName,
    "--version", $Version, "--environment", $Environment, "--channel", $Channel,
    "--platform", $Platform, "--entry-point", $EntryPoint, "--catalog-profile", $CatalogProfile,
    "--allowed-profiles", ($AllowedClientProfiles -join ",")
)
if (![string]::IsNullOrWhiteSpace($Notes)) { $arguments += @("--notes", $Notes) }
if (![string]::IsNullOrWhiteSpace($PrivateKeyPath)) { $arguments += @("--private-key", $PrivateKeyPath) }
if (![string]::IsNullOrWhiteSpace($KeyId)) { $arguments += @("--key-id", $KeyId) }
if ($SetLatest) { $arguments += "--set-latest" }
if ($DryRun) { $arguments += "--dry-run" }
if ($Replace) { $arguments += "--replace" }
if ($AllowUnsigned) { $arguments += "--allow-unsigned" }
$process = Start-Process -FilePath $launcher -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Atomic publish failed with exit code $($process.ExitCode)" }
Write-Host "Atomic release transaction completed."
if ($DryRun -or [string]::IsNullOrWhiteSpace($Remote)) { return }

$separatorIndex = $Remote.IndexOf(":")
if ($separatorIndex -lt 1) { throw "Remote must look like user@host:/srv/ue-dt-updates" }
$remoteHost = $Remote.Substring(0, $separatorIndex)
$remoteRoot = $Remote.Substring($separatorIndex + 1).TrimEnd('/')
$releaseRelative = "projects/$ProjectId/$Environment/$Channel/$Version/$Platform"
$releaseDir = Join-Path $ServerRoot ($releaseRelative.Replace('/', [IO.Path]::DirectorySeparatorChar))
$catalogDir = Join-Path $ServerRoot ("catalogs/$CatalogProfile".Replace('/', [IO.Path]::DirectorySeparatorChar))
$ssh = Get-Command ssh -ErrorAction Stop
$rsync = Get-Command rsync -ErrorAction SilentlyContinue
$scp = Get-Command scp -ErrorAction SilentlyContinue
if ($null -eq $rsync -and $null -eq $scp) { throw "Remote upload requires rsync or scp." }
& $ssh.Source $remoteHost "mkdir -p '$remoteRoot/$releaseRelative' '$remoteRoot/catalogs/$CatalogProfile'"
if ($LASTEXITCODE -ne 0) { throw "Remote directory creation failed." }
if ($null -ne $rsync) {
    & $rsync.Source -az --delete (($releaseDir -replace '\\', '/') + '/') "${remoteHost}:$remoteRoot/$releaseRelative/"
    if ($LASTEXITCODE -ne 0) { throw "Release upload failed." }
    & $rsync.Source -az (($catalogDir -replace '\\', '/') + '/') "${remoteHost}:$remoteRoot/catalogs/$CatalogProfile/"
    if ($LASTEXITCODE -ne 0) { throw "Catalog upload failed." }
}
else {
    & $scp.Source -r (Join-Path $releaseDir "*") "${remoteHost}:$remoteRoot/$releaseRelative/"
    if ($LASTEXITCODE -ne 0) { throw "Release upload failed." }
    & $scp.Source -r (Join-Path $catalogDir "*") "${remoteHost}:$remoteRoot/catalogs/$CatalogProfile/"
    if ($LASTEXITCODE -ne 0) { throw "Catalog upload failed." }
}
Write-Host "Remote release and catalog upload completed."
