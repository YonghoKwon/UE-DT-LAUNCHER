param([Parameter(Mandatory = $true)][string[]]$Files, [switch]$RequireSignature, [switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WindowsPackaging.psm1') -Force
if ($Files.Count -eq 0) { throw 'No signing inputs.' }
foreach ($file in $Files) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing signing input: $file" } }
if ([string]::IsNullOrWhiteSpace($env:UE_DT_SIGNING_CERT_THUMBPRINT) -and -not $RequireSignature -and -not $VerifyOnly) {
    Write-Warning 'No code-signing certificate configured. Files remain UNSIGNED-DEV.'
    return
}
$signer = Get-LauncherSigner
Invoke-LauncherSigning -Files $Files -Signer $signer -VerifyOnly:$VerifyOnly
