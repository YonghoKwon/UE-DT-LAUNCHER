param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$ArtifactsRoot = "artifacts/windows-installer",
    [switch]$OfficialBuild
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot $ArtifactsRoot
$payload = Join-Path $artifacts "payload"
$gui = Join-Path $payload "gui"
$agent = Join-Path $payload "agent"
New-Item -ItemType Directory -Force $gui, $agent | Out-Null
if ($OfficialBuild) { $official = "true"; $label = "SIGNED"; $buildInfo = "OFFICIAL BUILD - AUTHENTICODE SIGNING REQUIRED" }
else { $official = "false"; $label = "UNSIGNED-DEV"; $buildInfo = "UNSIGNED DEVELOPMENT BUILD - NOT FOR PRODUCTION" }
dotnet publish (Join-Path $repoRoot "src/UeDtLauncher/UeDtLauncher.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:LauncherVersion=$Version -p:LauncherOfficialBuild=$official -o $gui
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed." }
dotnet publish (Join-Path $repoRoot "src/UeDtLauncher.Agent/UeDtLauncher.Agent.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:LauncherVersion=$Version -p:LauncherOfficialBuild=$official -o $agent
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }
Set-Content -LiteralPath (Join-Path $payload "BUILD-INFO.txt") -Encoding UTF8 -Value $buildInfo
dotnet build (Join-Path $repoRoot "installer/windows/UeDtLauncher.Installer.wixproj") -c $Configuration -p:ProductVersion=$Version -p:PublishRoot=$payload -p:BuildLabel=$label -o $artifacts
if ($LASTEXITCODE -ne 0) { throw "MSI build failed." }
Write-Host "Windows installer artifacts: $artifacts"
