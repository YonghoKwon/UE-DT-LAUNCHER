Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LauncherSigner {
    $thumbprint = ($env:UE_DT_SIGNING_CERT_THUMBPRINT -replace '\s','').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Official build requires UE_DT_SIGNING_CERT_THUMBPRINT.' }
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
    $now = Get-Date
    if (-not $cert.HasPrivateKey -or $cert.NotBefore -gt $now -or $cert.NotAfter -le $now) { throw 'Signing certificate/private key is unavailable or outside its validity period.' }
    if ('1.3.6.1.5.5.7.3.3' -notin @($cert.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })) { throw 'A Code Signing EKU is required.' }
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse |
        Where-Object FullName -Match '\\x64\\signtool.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $tool) { throw 'x64 signtool.exe is unavailable.' }
    $timestamp = $env:UE_DT_TIMESTAMP_URL
    if ([string]::IsNullOrWhiteSpace($timestamp)) { $timestamp = 'https://timestamp.digicert.com' }
    $uri = [uri]$timestamp
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo) { throw 'Invalid RFC 3161 timestamp URL.' }
    return @{ Tool = $tool.FullName; Thumbprint = $thumbprint; Timestamp = $timestamp }
}

function Invoke-PackageTool([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Packaging tool failed ($LASTEXITCODE): $Tool" }
}

function Assert-LauncherSignature([string]$File, $Signer) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Signature input is missing: $File" }
    Invoke-PackageTool $Signer.Tool @('verify','/pa','/all','/tw',$File)
    $signature = Get-AuthenticodeSignature -LiteralPath $File
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $Signer.Thumbprint -or $null -eq $signature.TimeStamperCertificate) {
        throw "Expected signer and trusted timestamp were not verified: $File"
    }
}

function Invoke-LauncherSigning([string[]]$Files, $Signer, [switch]$VerifyOnly) {
    if ($Files.Count -eq 0) { throw 'Signing requires at least one exact file.' }
    foreach ($file in $Files) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Signing input is missing: $file" }
        if (-not $VerifyOnly) { Invoke-PackageTool $Signer.Tool @('sign','/sha1',$Signer.Thumbprint,'/fd','SHA256','/tr',$Signer.Timestamp,'/td','SHA256',$file) }
        Assert-LauncherSignature $file $Signer
    }
}

function Publish-LauncherPayload($Context) {
    foreach ($item in @(@('UeDtLauncher','gui'), @('UeDtLauncher.Agent','agent'))) {
        Invoke-PackageTool 'dotnet' @('publish', (Join-Path $Context.Repo "src/$($item[0])/$($item[0]).csproj"), '-c', $Context.Configuration,
            '-r','win-x64','--self-contained','true','-p:PublishSingleFile=true',"-p:LauncherVersion=$($Context.Version)",
            "-p:LauncherOfficialBuild=$($Context.Official.ToString().ToLowerInvariant())",'-o',(Join-Path $Context.Payload $item[1]))
    }
    Set-Content -LiteralPath (Join-Path $Context.Payload 'BUILD-INFO.txt') -Encoding UTF8 -Value $(if ($Context.Official) { 'OFFICIAL BUILD - SIGNATURE VALIDATION REQUIRED' } else { 'UNSIGNED DEVELOPMENT BUILD - NOT FOR PRODUCTION' })
    Copy-Item -LiteralPath (Join-Path $Context.Repo 'examples/configs/distribution-general-windows.config.json') -Destination (Join-Path $Context.Payload 'launcher.config.example.json')
}

function New-LauncherMsi($Context) {
    Invoke-PackageTool 'dotnet' @('build',(Join-Path $Context.Repo 'installer/windows/UeDtLauncher.Installer.wixproj'),'-c',$Context.Configuration,
        "-p:ProductVersion=$($Context.Version)","-p:PublishRoot=$($Context.Payload)","-p:BuildLabel=$($Context.Label)",
        "-p:BaseIntermediateOutputPath=$(Join-Path $Context.Run 'wix-obj')/",'-o',$Context.Output)
    if (-not (Test-Path -LiteralPath $Context.Msi -PathType Leaf)) { throw 'This run did not produce its expected MSI.' }
}

function Test-LauncherMsiPayload($Context) {
    $arguments = @{ Msi = $Context.Msi; Gui = $Context.Gui; Agent = $Context.Agent; OutputDirectory = (Join-Path $Context.Run 'extracted') }
    $report = & (Join-Path $Context.Repo 'scripts/verify-msi-payload.ps1') @arguments
    if ($Context.Official) { Invoke-LauncherSigning @($report.Gui, $report.Agent) $Context.Signer -VerifyOnly }
    return $report
}

# Private command seams are replaced only inside test module sessions. No public bypass flag.
function Invoke-LauncherPackageBuild([string]$Repo, [string]$Artifacts, [string]$Version, [string]$Configuration, [bool]$Official) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'MSI Version must be numeric major.minor.patch.' }
    $signer = if ($Official) { Get-LauncherSigner } else { $null }
    $run = Join-Path $Artifacts ('runs/' + [guid]::NewGuid().ToString('N'))
    $label = if ($Official) { 'SIGNED' } else { 'UNSIGNED-DEV' }
    $context = @{ Repo=$Repo; Run=$run; Payload=(Join-Path $run 'payload'); Output=(Join-Path $run 'output');
        Version=$Version; Configuration=$Configuration; Official=$Official; Label=$label; Signer=$signer }
    $context.Gui = Join-Path $context.Payload 'gui/UeDtLauncher.exe'
    $context.Agent = Join-Path $context.Payload 'agent/UeDtLauncher.Agent.exe'
    $context.Msi = Join-Path $context.Output "UeDtLauncher-$Version-$label-x64.msi"
    New-Item -ItemType Directory -Force $context.Payload, $context.Output | Out-Null
    Publish-LauncherPayload $context
    if ($Official) { Invoke-LauncherSigning @($context.Gui,$context.Agent) $signer }
    New-LauncherMsi $context
    if ($Official) { Invoke-LauncherSigning @($context.Msi) $signer }
    $verification = Test-LauncherMsiPayload $context
    $release = Join-Path $run 'release'
    New-Item -ItemType Directory -Path $release | Out-Null
    foreach ($file in @($context.Gui,$context.Agent,$context.Msi,(Join-Path $context.Payload 'BUILD-INFO.txt'),(Join-Path $context.Payload 'launcher.config.example.json'))) {
        Copy-Item -LiteralPath $file -Destination $release
    }
    $result = [ordered]@{ version=$Version; label=$label; official=$Official; artifactDirectory=$release;
        msi=(Join-Path $release (Split-Path $context.Msi -Leaf)); payloadVerified=$true; payloadHashes=$verification.Hashes }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $release 'package-result.json') -Encoding UTF8
    if ($env:GITHUB_ENV) { "UE_DT_PACKAGE_DIR=$release" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding UTF8 }
    return [pscustomobject]$result
}

Export-ModuleMember -Function Invoke-LauncherPackageBuild, Get-LauncherSigner, Invoke-LauncherSigning
