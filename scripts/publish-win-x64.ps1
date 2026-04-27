param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "publish/win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src/UeDtLauncher/UeDtLauncher.csproj"
$output = Join-Path $repoRoot $OutputDir

Write-Host "Publishing UE-DT-LAUNCHER for Windows x64..."
Write-Host "Project: $project"
Write-Host "Output : $output"

dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $output

Write-Host ""
Write-Host "Done. Run:"
Write-Host "  $output\UeDtLauncher.exe"
