param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir,

    [Parameter(Mandatory = $true)]
    [string]$ServerRoot,

    [Parameter(Mandatory = $true)]
    [string]$BaseUrlRoot,

    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet("prod", "dev")]
    [string]$Environment,

    [Parameter(Mandatory = $true)]
    [ValidateSet("stable", "beta", "dev")]
    [string]$Channel,

    [Parameter(Mandatory = $true)]
    [ValidateSet("windows-x64", "linux-x64")]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$EntryPoint,

    [Parameter(Mandatory = $true)]
    [ValidateSet("general", "developer")]
    [string]$CatalogProfile,

    [string[]]$AllowedClientProfiles,

    [string]$Notes = "",

    [switch]$SetLatest,

    [switch]$CleanFiles,

    [switch]$NoCopy,

    # Optional remote upload target, e.g. "deploy@updates.example.com:/srv/ue-dt-updates".
    # Requires rsync or scp+ssh available on this machine (e.g. Git for Windows, OpenSSH).
    [string]$Remote = $null
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$generateManifest = Join-Path $scriptDir "generate-manifest.ps1"
$updateCatalog = Join-Path $scriptDir "update-catalog.ps1"

if (!(Test-Path $generateManifest -PathType Leaf)) { throw "generate-manifest.ps1 not found: $generateManifest" }
if (!(Test-Path $updateCatalog -PathType Leaf)) { throw "update-catalog.ps1 not found: $updateCatalog" }
if (!(Test-Path $PackageDir -PathType Container)) { throw "PackageDir does not exist: $PackageDir" }

if ($null -eq $AllowedClientProfiles -or $AllowedClientProfiles.Count -eq 0) {
    $AllowedClientProfiles = @($CatalogProfile)
}

$BaseUrlRoot = $BaseUrlRoot.TrimEnd('/')
$releaseRelative = "projects/$ProjectId/$Environment/$Channel/$Version/$Platform"
$releaseDir = Join-Path $ServerRoot ($releaseRelative.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
$filesDir = Join-Path $releaseDir "files"
$manifestPath = Join-Path $releaseDir "manifest.json"
$catalogPath = Join-Path $ServerRoot ("catalogs/$CatalogProfile/catalog.json".Replace('/', [System.IO.Path]::DirectorySeparatorChar))
$manifestUrl = "$BaseUrlRoot/$releaseRelative/manifest.json"
$filesBaseUrl = "$BaseUrlRoot/$releaseRelative/files"

New-Item -ItemType Directory -Force $releaseDir | Out-Null

if (!$NoCopy) {
    if ($CleanFiles -and (Test-Path $filesDir)) {
        Remove-Item $filesDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force $filesDir | Out-Null
    Copy-Item (Join-Path $PackageDir "*") $filesDir -Recurse -Force
    Write-Host "Package copied"
    Write-Host "  From: $PackageDir"
    Write-Host "  To  : $filesDir"
}
else {
    if (!(Test-Path $filesDir -PathType Container)) {
        throw "NoCopy was specified, but files directory does not exist: $filesDir"
    }
}

& $generateManifest `
    -PackageDir $filesDir `
    -Output $manifestPath `
    -ProjectId $ProjectId `
    -Version $Version `
    -Channel $Channel `
    -Platform $Platform `
    -EntryPoint $EntryPoint `
    -BaseUrl $filesBaseUrl

$catalogArgs = @(
    "-CatalogPath", $catalogPath,
    "-ProjectId", $ProjectId,
    "-DisplayName", $DisplayName,
    "-Version", $Version,
    "-Environment", $Environment,
    "-Channel", $Channel,
    "-Platform", $Platform,
    "-ManifestUrl", $manifestUrl,
    "-AllowedClientProfiles", $AllowedClientProfiles,
    "-Notes", $Notes
)

if ($SetLatest) {
    $catalogArgs += "-SetLatest"
}

& $updateCatalog @catalogArgs

if (![string]::IsNullOrWhiteSpace($Remote)) {
    $separatorIndex = $Remote.IndexOf(":")
    if ($separatorIndex -lt 1) {
        throw "Remote must look like user@host:/srv/ue-dt-updates"
    }
    $remoteHost = $Remote.Substring(0, $separatorIndex)
    $remoteRoot = $Remote.Substring($separatorIndex + 1)

    $rsync = Get-Command rsync -ErrorAction SilentlyContinue
    $scp = Get-Command scp -ErrorAction SilentlyContinue
    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -eq $ssh -or ($null -eq $rsync -and $null -eq $scp)) {
        throw "Remote upload requires ssh plus rsync or scp on this machine."
    }

    Write-Host "Uploading release to $Remote ..."
    & $ssh.Source $remoteHost "mkdir -p '$remoteRoot/$releaseRelative' '$remoteRoot/catalogs/$CatalogProfile'"
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed with exit code $LASTEXITCODE" }

    $catalogDir = Split-Path -Parent $catalogPath
    if ($null -ne $rsync) {
        # rsync on Windows expects cygwin-style paths from Git Bash; forward slashes work for both.
        $releaseSrc = ($releaseDir -replace "\\", "/") + "/"
        $catalogSrc = ($catalogDir -replace "\\", "/") + "/"
        & $rsync.Source -az --delete $releaseSrc "${remoteHost}:$remoteRoot/$releaseRelative/"
        if ($LASTEXITCODE -ne 0) { throw "rsync release upload failed with exit code $LASTEXITCODE" }
        & $rsync.Source -az $catalogSrc "${remoteHost}:$remoteRoot/catalogs/$CatalogProfile/"
        if ($LASTEXITCODE -ne 0) { throw "rsync catalog upload failed with exit code $LASTEXITCODE" }
    }
    else {
        & $scp.Source -r (Join-Path $releaseDir "*") "${remoteHost}:$remoteRoot/$releaseRelative/"
        if ($LASTEXITCODE -ne 0) { throw "scp release upload failed with exit code $LASTEXITCODE" }
        & $scp.Source -r (Join-Path $catalogDir "*") "${remoteHost}:$remoteRoot/catalogs/$CatalogProfile/"
        if ($LASTEXITCODE -ne 0) { throw "scp catalog upload failed with exit code $LASTEXITCODE" }
    }
    Write-Host "Upload completed."
}

Write-Host "Release published"
Write-Host "  ServerRoot : $ServerRoot"
Write-Host "  Catalog    : $catalogPath"
Write-Host "  ReleaseDir : $releaseDir"
Write-Host "  Manifest   : $manifestPath"
Write-Host "  ManifestUrl: $manifestUrl"
Write-Host "  FilesUrl   : $filesBaseUrl"
