param(
    [Parameter(Mandatory = $true)]
    [string]$CatalogPath,

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
    [string]$ManifestUrl,

    [Parameter(Mandatory = $true)]
    [string[]]$AllowedClientProfiles,

    [string]$Notes = "",

    [string]$ManifestSignatureUrl = $null,

    [switch]$SetLatest,

    [switch]$RemoveRelease,

    [switch]$RemoveProjectIfEmpty
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function ConvertTo-HashtableDeep {
    param([Parameter(Mandatory = $true)]$InputObject)

    if ($null -eq $InputObject) { return $null }

    if ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string] -and $InputObject -isnot [pscustomobject]) {
        $array = @()
        foreach ($item in $InputObject) {
            $array += ConvertTo-HashtableDeep $item
        }
        return $array
    }

    if ($InputObject -is [pscustomobject]) {
        $hash = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $hash[$property.Name] = ConvertTo-HashtableDeep $property.Value
        }
        return $hash
    }

    return $InputObject
}

function New-EmptyCatalog {
    return [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToUniversalTime().ToString("o")
        projects = @()
    }
}

function Find-ProjectIndex {
    param($Projects, [string]$ProjectId)
    for ($i = 0; $i -lt $Projects.Count; $i++) {
        if ([string]::Equals($Projects[$i].projectId, $ProjectId, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $i
        }
    }
    return -1
}

function Find-ReleaseIndex {
    param($Releases, [string]$Version, [string]$Environment, [string]$Channel, [string]$Platform)
    for ($i = 0; $i -lt $Releases.Count; $i++) {
        $release = $Releases[$i]
        if ([string]::Equals($release.version, $Version, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($release.environment, $Environment, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($release.channel, $Channel, [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($release.platform, $Platform, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $i
        }
    }
    return -1
}

$catalogDir = Split-Path -Parent $CatalogPath
if (![string]::IsNullOrWhiteSpace($catalogDir)) {
    New-Item -ItemType Directory -Force $catalogDir | Out-Null
}

if (Test-Path $CatalogPath -PathType Leaf) {
    $raw = Get-Content $CatalogPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        $catalog = New-EmptyCatalog
    }
    else {
        $catalog = ConvertTo-HashtableDeep ($raw | ConvertFrom-Json)
        if (!$catalog.Contains("projects") -or $null -eq $catalog.projects) { $catalog.projects = @() }
    }
}
else {
    $catalog = New-EmptyCatalog
}

$projects = @($catalog.projects)
$projectIndex = Find-ProjectIndex -Projects $projects -ProjectId $ProjectId

if ($projectIndex -lt 0 -and $RemoveRelease) {
    Write-Host "Project not found. Nothing to remove: $ProjectId"
    $catalog.generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    $catalog.projects = @($projects)
    $catalog | ConvertTo-Json -Depth 50 | Set-Content $CatalogPath -Encoding UTF8
    return
}

if ($projectIndex -lt 0) {
    $projects += [ordered]@{
        projectId = $ProjectId
        displayName = $DisplayName
        releases = @()
    }
    $projectIndex = $projects.Count - 1
}

$project = $projects[$projectIndex]
$project.displayName = $DisplayName
if (!$project.Contains("releases") -or $null -eq $project.releases) { $project.releases = @() }

$releases = @($project.releases)
$releaseIndex = Find-ReleaseIndex -Releases $releases -Version $Version -Environment $Environment -Channel $Channel -Platform $Platform

if ($RemoveRelease) {
    if ($releaseIndex -ge 0) {
        $updatedReleases = @()
        for ($i = 0; $i -lt $releases.Count; $i++) {
            if ($i -ne $releaseIndex) { $updatedReleases += $releases[$i] }
        }
        $releases = $updatedReleases
        Write-Host "Release removed: $ProjectId $Version $Environment/$Channel/$Platform"
    }
    else {
        Write-Host "Release not found. Nothing to remove: $ProjectId $Version $Environment/$Channel/$Platform"
    }

    if ($RemoveProjectIfEmpty -and $releases.Count -eq 0) {
        $updatedProjects = @()
        for ($i = 0; $i -lt $projects.Count; $i++) {
            if ($i -ne $projectIndex) { $updatedProjects += $projects[$i] }
        }
        $projects = $updatedProjects
        Write-Host "Project removed because it has no releases: $ProjectId"
    }
    else {
        $project.releases = @($releases)
        $projects[$projectIndex] = $project
    }
}
else {
    if ($SetLatest) {
        for ($i = 0; $i -lt $releases.Count; $i++) {
            $release = $releases[$i]
            if ([string]::Equals($release.environment, $Environment, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($release.channel, $Channel, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals($release.platform, $Platform, [System.StringComparison]::OrdinalIgnoreCase)) {
                $release.isLatest = $false
                $releases[$i] = $release
            }
        }
    }

    $newRelease = [ordered]@{
        version = $Version
        channel = $Channel
        environment = $Environment
        platform = $Platform
        manifestUrl = $ManifestUrl
        manifestSignatureUrl = $ManifestSignatureUrl
        allowedClientProfiles = @($AllowedClientProfiles)
        isLatest = [bool]$SetLatest
        notes = $Notes
    }

    if ($releaseIndex -ge 0) {
        $releases[$releaseIndex] = $newRelease
        Write-Host "Release updated: $ProjectId $Version $Environment/$Channel/$Platform"
    }
    else {
        $releases += $newRelease
        Write-Host "Release added: $ProjectId $Version $Environment/$Channel/$Platform"
    }

    $project.releases = @($releases)
    $projects[$projectIndex] = $project
}

$catalog.generatedAt = (Get-Date).ToUniversalTime().ToString("o")
$catalog.projects = @($projects)
$catalog | ConvertTo-Json -Depth 50 | Set-Content $CatalogPath -Encoding UTF8

Write-Host "Catalog updated"
Write-Host "  Catalog : $CatalogPath"
Write-Host "  Project : $ProjectId"
Write-Host "  Version : $Version"
Write-Host "  Env/Ch  : $Environment / $Channel"
Write-Host "  Platform: $Platform"
