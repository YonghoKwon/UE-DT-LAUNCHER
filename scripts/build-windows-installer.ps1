param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release',
    [string]$ArtifactsRoot = 'artifacts/windows-installer',
    [switch]$OfficialBuild
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsPackaging.psm1') -Force
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactsRoot))
Invoke-LauncherPackageBuild -Repo $repoRoot -Artifacts $artifacts -Version $Version -Configuration $Configuration -Official $OfficialBuild.IsPresent
