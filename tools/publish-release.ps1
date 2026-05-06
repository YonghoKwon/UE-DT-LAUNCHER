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

    [switch]$NoCopy
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

Write-Host "Release published"
Write-Host "  ServerRoot : $ServerRoot"
Write-Host "  Catalog    : $catalogPath"
Write-Host "  ReleaseDir : $releaseDir"
Write-Host "  Manifest   : $manifestPath"
Write-Host "  ManifestUrl: $manifestUrl"
Write-Host "  FilesUrl   : $filesBaseUrl"
