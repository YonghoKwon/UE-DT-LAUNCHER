param([Parameter(Mandatory = $true)][string[]]$Files, [switch]$RequireSignature)
$ErrorActionPreference = "Stop"
$thumbprint = $env:UE_DT_SIGNING_CERT_THUMBPRINT
$timestampUrl = $env:UE_DT_TIMESTAMP_URL
if ([string]::IsNullOrWhiteSpace($timestampUrl)) { $timestampUrl = "https://timestamp.digicert.com" }
if ([string]::IsNullOrWhiteSpace($thumbprint)) {
    if ($RequireSignature) { throw "Stable release requires UE_DT_SIGNING_CERT_THUMBPRINT." }
    Write-Warning "No code-signing certificate configured. Files remain UNSIGNED-DEV."
    return
}
$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction Stop |
    Where-Object FullName -Match '\\x64\\signtool.exe$' | Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $signTool) { throw "signtool.exe was not found." }
foreach ($file in $Files) {
    & $signTool.FullName sign /sha1 $thumbprint /fd SHA256 /tr $timestampUrl /td SHA256 $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
    & $signTool.FullName verify /pa /all $file
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $file" }
}
