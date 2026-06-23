param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDir,

    [Parameter(Mandatory = $true)]
    [string]$Output,

    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet("stable", "beta", "dev")]
    [string]$Channel,

    [Parameter(Mandatory = $true)]
    [ValidateSet("windows-x64", "linux-x64")]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$EntryPoint,

    [Parameter(Mandatory = $true)]
    [string]$BaseUrl
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Convert-ToRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    $resolvedRoot = (Resolve-Path $Root).Path.TrimEnd('\', '/')
    $resolvedPath = (Resolve-Path $FullPath).Path

    if (!$resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not under root. Root=$resolvedRoot Path=$resolvedPath"
    }

    return $resolvedPath.Substring($resolvedRoot.Length).TrimStart('\', '/').Replace('\', '/')
}

function Assert-FileExistsInPackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $nativeRelative = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = Join-Path $PackageRoot $nativeRelative
    if (!(Test-Path $fullPath -PathType Leaf)) {
        throw "EntryPoint was not found under PackageDir: $RelativePath ($fullPath)"
    }
}

if (!(Test-Path $PackageDir -PathType Container)) {
    throw "PackageDir does not exist: $PackageDir"
}

$resolvedPackageDir = (Resolve-Path $PackageDir).Path
$EntryPoint = $EntryPoint.Replace('\', '/')
Assert-FileExistsInPackage -PackageRoot $resolvedPackageDir -RelativePath $EntryPoint

$files = Get-ChildItem $resolvedPackageDir -File -Recurse | Sort-Object FullName
if ($files.Count -eq 0) {
    throw "No files found under PackageDir: $resolvedPackageDir"
}

$manifestFiles = @()
foreach ($file in $files) {
    $relativePath = Convert-ToRelativePath -Root $resolvedPackageDir -FullPath $file.FullName
    $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $isExecutable = $false
    if ($Platform -eq "windows-x64") {
        $isExecutable = $relativePath.ToLowerInvariant().EndsWith(".exe")
    }
    elseif ($Platform -eq "linux-x64") {
        $isExecutable = $relativePath -eq $EntryPoint
    }

    $manifestFiles += [ordered]@{
        path = $relativePath
        sha256 = $sha
        size = $file.Length
        url = $relativePath
        executable = $isExecutable
    }
}

$manifest = [ordered]@{
    appId = $ProjectId
    version = $Version
    channel = $Channel
    platform = $Platform
    entryPoint = $EntryPoint
    baseUrl = $BaseUrl.TrimEnd('/')
    files = @($manifestFiles)
}

$outputDir = Split-Path -Parent $Output
if (![string]::IsNullOrWhiteSpace($outputDir)) {
    New-Item -ItemType Directory -Force $outputDir | Out-Null
}

$manifest | ConvertTo-Json -Depth 50 | Set-Content $Output -Encoding UTF8

Write-Host "Manifest generated"
Write-Host "  Output    : $Output"
Write-Host "  ProjectId : $ProjectId"
Write-Host "  Version   : $Version"
Write-Host "  Channel   : $Channel"
Write-Host "  Platform  : $Platform"
Write-Host "  EntryPoint: $EntryPoint"
Write-Host "  Files     : $($manifestFiles.Count)"
