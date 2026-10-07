$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $repo 'scripts/WindowsPackaging.psm1'
$root = Join-Path $repo ('publish/packaging-tests/' + [guid]::NewGuid().ToString('N'))
$saved = $env:UE_DT_SIGNING_CERT_THUMBPRINT
$savedGithub = $env:GITHUB_ENV
try {
    $env:UE_DT_SIGNING_CERT_THUMBPRINT = ''; $env:GITHUB_ENV = ''
    $preflight = Join-Path $root 'preflight'
    $failed = $false
    try { & (Join-Path $repo 'scripts/build-windows-installer.ps1') -OfficialBuild -ArtifactsRoot $preflight | Out-Null }
    catch { $failed = $true }
    if (-not $failed -or (Test-Path -LiteralPath $preflight)) { throw 'Missing credentials did not stop before output creation.' }
    foreach ($failure in @('', 'publish', 'sign-payload', 'msi', 'sign-msi', 'payload-verify')) {
        $module = Import-Module $modulePath -Force -PassThru
        & $module {
            param($fail)
            $script:events = [Collections.Generic.List[string]]::new(); $script:failure = $fail
            function script:Event($name) { $script:events.Add($name); if ($name -eq $script:failure) { throw "fixture failure: $name" } }
            function script:Get-LauncherSigner { Event 'preflight'; return @{ Thumbprint='fixture' } }
            function script:Publish-LauncherPayload($Context) {
                Event 'publish'
                foreach ($path in @($Context.Gui,$Context.Agent,(Join-Path $Context.Payload 'BUILD-INFO.txt'),(Join-Path $Context.Payload 'launcher.config.example.json'))) {
                    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
                    [IO.File]::WriteAllText($path, 'test fixture, not a real signed artifact')
                }
            }
            function script:Invoke-LauncherSigning([string[]]$Files, $Signer, [switch]$VerifyOnly) {
                if ($Files.Count -eq 2) { Event 'sign-payload' } else { Event 'sign-msi' }
            }
            function script:New-LauncherMsi($Context) { Event 'msi'; [IO.File]::WriteAllText($Context.Msi, 'not an MSI') }
            function script:Test-LauncherMsiPayload($Context) { Event 'payload-verify'; return @{ Hashes=@{} } }
        } $failure
        $artifacts = Join-Path $root ('case-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $artifacts 'stale.msi'), 'must never be selected')
        $failed = $false
        try { $result = Invoke-LauncherPackageBuild $repo $artifacts '1.2.3' 'Release' $true }
        catch { $failed = $true }
        $actual = (& $module { $script:events.ToArray() }) -join ','
        $expected = @('preflight','publish','sign-payload','msi','sign-msi','payload-verify')
        if ($failure) { $expected = $expected[0..[array]::IndexOf($expected,$failure)] }
        if ($actual -ne ($expected -join ',') -or $failed -ne [bool]$failure) { throw "Wrong execution/failure sequence: $actual" }
        if ($failure -and @(Get-ChildItem $artifacts -Filter package-result.json -Recurse).Count -ne 0) { throw 'Failed build published a result.' }
        if (-not $failure -and $result.msi -like '*stale*') { throw 'Stale MSI selected.' }
    }
    # Exercise the real verification gate with replaced OS/tool boundaries, not a production flag.
    $module = Import-Module $modulePath -Force -PassThru
    & $module {
        param($root)
        function script:Invoke-PackageTool { }
        function script:Get-AuthenticodeSignature {
            [pscustomobject]@{ Status=$script:testStatus; SignerCertificate=[pscustomobject]@{ Thumbprint=$script:testSigner }; TimeStamperCertificate=$script:testTimestamp }
        }
        $file = Join-Path $root 'verification.exe'; [IO.File]::WriteAllText($file, 'fixture')
        foreach ($case in @('valid','wrong-signer','missing-timestamp','invalid')) {
            $script:testStatus = if ($case -eq 'invalid') { 'NotTrusted' } else { 'Valid' }
            $script:testSigner = if ($case -eq 'wrong-signer') { 'wrong' } else { 'expected' }
            $script:testTimestamp = if ($case -eq 'missing-timestamp') { $null } else { 'fixture' }
            $failed = $false
            try { Assert-LauncherSignature $file @{Tool='mock';Thumbprint='expected'} } catch { $failed=$true }
            if ($failed -ne ($case -ne 'valid')) { throw "Verification gate failed: $case" }
        }
    } $root
    Write-Output 'PASS: 11 packaging preflight/order/failure/signature-contract cases. Mock cases are not Authenticode evidence.'
} finally {
    $env:UE_DT_SIGNING_CERT_THUMBPRINT = $saved; $env:GITHUB_ENV = $savedGithub
    Remove-Module WindowsPackaging -ErrorAction SilentlyContinue
}
