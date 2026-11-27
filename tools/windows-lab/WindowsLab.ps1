#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position=0)]
    [ValidateSet('Start','Stop','Status','Add-Client','Publish')]
    [string]$Action,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'UeDtWindowsLab'),
    [string]$ServerExe, [string]$LauncherExe, [string]$CaddyZip, [string]$Address,
    [ValidateRange(1,65535)][int]$Port = 18080,
    [ValidateRange(1,65535)][int]$BackendPort = 18500,
    [string]$Enrollment, [string]$ClientAddress, [string]$Project,
    [ValidatePattern('\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z')]
    [string]$CredentialName = 'ue-dt-win-lab',
    [string]$Environment = 'prod', [string]$Channel = 'stable',
    [ValidateSet('WindowsClient','WindowsStreaming','LinuxStreaming')]
    [string]$ClientKind = 'WindowsClient',
    [switch]$Developer,
    [string]$Zip, [string]$Version, [string]$PayloadRoot, [string]$EntryPoint,
    [ValidateSet('windows-x64','linux-x64')][string]$Platform = 'windows-x64',
    [string]$ExecutablePaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1 (powershell.exe).' }
# Official GitHub release asset digest, then the executable digest from the verified archive.
$archiveHash = 'AD93112D335EE687100C4C9F11BCED16CA590377976B7FE7883882B9E797E472'
$caddyHash = '2622E239C79DF700B0EB92CBD950D1EDEE365E155A7859F358F0E55F002DF591'
$utf8 = [Text.UTF8Encoding]::new($false)
$Root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
$marker = Join-Path $Root 'lab.json'
$config = Join-Path $Root 'server.json'
$store = Join-Path $Root 'store'
$incoming = Join-Path $store 'incoming'
$publicKey = Join-Path $Root 'release-public.pem'
$privateKey = Join-Path $Root 'signing.pem'
$policy = Join-Path $Root 'access-policy.json'
$caddy = Join-Path $Root 'caddy.exe'
$caddyfile = Join-Path $Root 'Caddyfile'
$journal = Join-Path $Root 'processes.json'
if ($Developer) {
    if ($Action -ne 'Add-Client' -or $ClientKind -ne 'WindowsClient' -or
        $PSBoundParameters.ContainsKey('Environment') -or $PSBoundParameters.ContainsKey('Channel')) {
        throw '-Developer is for WindowsClient registration and sets the dev:beta grant.'
    }
    $Environment = 'dev'
    $Channel = 'beta'
}

function Assert-NoLink([string]$Path) {
    for ($item = $Path; $item; $item = [IO.Path]::GetDirectoryName($item)) {
        if ((Test-Path -LiteralPath $item) -and ([IO.File]::GetAttributes($item) -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse point is not supported: $item"
        }
    }
}

function Write-Json([string]$Path, $Value) {
    $temp = $Path + '.' + [guid]::NewGuid().ToString('N')
    $backup = $temp + '.backup'
    try {
        [IO.File]::WriteAllText($temp, (ConvertTo-Json -InputObject $Value -Depth 8), $utf8)
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, $backup) }
        else { [IO.File]::Move($temp, $Path) }
    } finally {
        [IO.File]::Delete($temp)
        [IO.File]::Delete($backup)
    }
}

function Read-Json([string]$Path) {
    Assert-NoLink $Path
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Assert-Hash([string]$Path, [string]$Sha) {
    Assert-NoLink $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha) {
        throw "Caddy SHA-256 mismatch: $Path. Use the official v2.11.9 Windows amd64 ZIP."
    }
}

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    # The Windows launcher is a GUI-subsystem executable. A pipeline makes PowerShell wait.
    & $Exe @Arguments | Write-Output
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) failed with exit $LASTEXITCODE." }
}

function Get-IPv4([string]$Value) {
    $ip = $null
    if (![Net.IPAddress]::TryParse($Value, [ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork' -or
        $ip.ToString() -cne $Value -or $ip.Equals([Net.IPAddress]::Any) -or $ip.GetAddressBytes()[0] -ge 224) {
        throw 'Specify a canonical unicast IPv4 address.'
    }
    $ip
}

function Assert-PortFree([Net.IPAddress]$IP, [int]$Number) {
    $listener = [Net.Sockets.TcpListener]::new($IP, $Number)
    try {
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
    } catch { throw "Cannot bind $($IP):$Number. Verify the address and check for another listener." }
    finally { $listener.Stop() }
}

function Get-HttpStatus([string]$Url) {
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.Proxy = $null
    $request.AllowAutoRedirect = $false
    $request.Timeout = 1000
    try { $response = $request.GetResponse() }
    catch [Net.WebException] {
        $response = $_.Exception.Response
        if (!$response) { return 0 }
    }
    try { [int]$response.StatusCode } finally { $response.Dispose() }
}

function Wait-Endpoints([uri]$Origin, [Diagnostics.Process]$Process) {
    $base = $Origin.GetLeftPart([UriPartial]::Authority)
    for ($i = 0; $i -lt 25; $i++) {
        if ($Process.HasExited) { throw "Process $($Process.Id) exited. See the lab logs in $Root." }
        if ((Get-HttpStatus "$base/api/v1/catalog") -eq 401 -and
            (Get-HttpStatus "$base/api/v1/auth/challenge?keyId=windows-lab-probe") -eq 200) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Challenge 200 and anonymous Catalog 401 were not observed at $base."
}

function Get-OwnedProcess($Record, [string]$Exe) {
    if (!$Record) { return }
    $process = Get-Process -Id ([int]$Record.pid) -ErrorAction SilentlyContinue
    if (!$process) { return }
    $owned = $false
    try {
        # Bind to an OS handle before checking creation time and image.
        $null = $process.Handle
        if (!$process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq [long]$Record.started) {
            if ($process.Path -ne $Exe) { throw 'Recorded process image does not match the lab executable.' }
            $owned = $true
            return $process
        }
    } finally {
        if (!$owned) { $process.Dispose() }
    }
}

function Start-LabProcess([string]$Exe, [string]$Arguments, [string]$Name) {
    $options = @{
        FilePath=$Exe; ArgumentList=$Arguments; WorkingDirectory=$Root; WindowStyle='Hidden'; PassThru=$true
        RedirectStandardOutput=(Join-Path $Root "$Name.out.log"); RedirectStandardError=(Join-Path $Root "$Name.err.log")
    }
    Start-Process @options
}

function Stop-LabProcess([Diagnostics.Process]$Process) {
    if ($Process -and !$Process.HasExited) {
        $Process.Kill()
        if (!$Process.WaitForExit(5000)) { throw "Owned process $($Process.Id) did not exit." }
    }
}

function Get-ProcessRecord([Diagnostics.Process]$Process) {
    @{ pid = $Process.Id; started = $Process.StartTime.ToUniversalTime().Ticks.ToString() }
}

function Show-Lab($Lab) {
    $public = [uri]$Lab.origin
    Write-Host "Distribution origin: $($Lab.origin)"
    Write-Host "Incoming: $incoming"
    if (![Net.IPAddress]::IsLoopback((Get-IPv4 $public.Host))) {
        Write-Host "Administrator PowerShell, if not already allowed:"
        Write-Host "New-NetFirewallRule -DisplayName 'UE DT Windows Lab $($public.Port)' -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $($public.Host) -LocalPort $($public.Port) -RemoteAddress LocalSubnet -Profile Private"
    }
}

function Initialize-Lab($Lab, [string]$Archive) {
    # Copy first so the exact bytes checked are the ones expanded.
    $localArchive = Join-Path $Root 'caddy.zip'
    [IO.File]::Copy($Archive, $localArchive)
    Assert-Hash $localArchive $archiveHash
    Expand-Archive -LiteralPath $localArchive -DestinationPath (Join-Path $Root 'caddy-dist')
    $source = Join-Path $Root 'caddy-dist\caddy.exe'
    Assert-Hash $source $caddyHash
    [IO.File]::Move($source, $caddy)
    [IO.File]::Delete($localArchive)
    Invoke-Checked $Lab.launcher @('generate-signing-key','--private-key',$privateKey,'--public-key',$publicKey) | Out-Null
    Write-Json $policy @{ clients = @() }
    [void][IO.Directory]::CreateDirectory($store)
    Write-Json $config @{
        root = $store; publicUrl = $Lab.origin; listenUrl = $Lab.backend
        signingKeyId = 'release-1'; signingKeyPath = $privateKey; policyPath = $policy
        authenticationMode = 'request-signature-v1'
    }
    Invoke-Checked $Lab.server @('list','--config',$config) | Out-Null
    $proxy = @"
{
    admin off
    persist_config off
}
$($Lab.origin) {
    bind $(([uri]$Lab.origin).Host)
    @delivery path /api/v1/auth/challenge /api/v1/catalog /releases/*
    handle @delivery {
        reverse_proxy $(([uri]$Lab.backend).Authority) {
            header_up X-Distribution-Client-IP {remote_host}
            header_up -X-Forwarded-For
            header_up -Forwarded
            header_up -X-Original-URI
            header_up -Authorization
            transport http {
                compression off
            }
        }
    }
    handle {
        respond 404
    }
}
"@
    [IO.File]::WriteAllText($caddyfile, $proxy, $utf8)
    Invoke-Checked $caddy @('validate','--config',$caddyfile,'--adapter','caddyfile') | Out-Null
    Write-Json $marker $Lab
}

function Add-LabClient($Lab) {
    $ip = Get-IPv4 $ClientAddress
    if (!$Project) { throw 'Specify -Project.' }
    $source = (Resolve-Path -LiteralPath $Enrollment).ProviderPath
    if ((Get-Item -LiteralPath $source).Length -gt 8192) { throw 'Enrollment file is too large.' }
    $stage = Join-Path $Root ('enroll-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    $copy = Join-Path $stage ([IO.Path]::GetFileName($source))
    $csv = Join-Path $stage 'addresses.csv'
    try {
        Assert-NoLink $source
        [IO.File]::Copy($source, $copy)
        $client = Read-Json $copy
        if ($client.clientId -cnotmatch '\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\z') { throw 'Invalid client ID.' }
        [IO.File]::WriteAllLines($csv, @('clientId,address', "$($client.clientId),$ip"), $utf8)
        $grant = '{0}:{1}:{2}' -f $Project,$Environment,$Channel
        $existing = (Read-Json $policy).clients | Where-Object id -CEQ $client.clientId
        if ($existing -and ($ip.ToString() -cnotin $existing.addresses -or
            !($existing.grants | Where-Object {
                $_.projectId -ceq $Project -and $_.environment -ceq $Environment -and $_.channel -ceq $Channel
            }))) {
            throw 'Existing client IP or grant differs. Enroll a new client ID or review the existing policy.'
        }
        $options = @('client-enroll','--from',$stage,'--address-source',"file:$csv",'--grant',$grant,'--config',$config)
        Invoke-Checked $Lab.server ($options + '--dry-run')
        Invoke-Checked $Lab.server $options
    } finally {
        [IO.File]::Delete($copy)
        [IO.File]::Delete($csv)
        [IO.Directory]::Delete($stage)
    }
    $bundle = Join-Path $Root ('client-' + $client.clientId)
    $pem = Join-Path $bundle 'release-public.pem'
    Assert-NoLink $pem
    [void][IO.Directory]::CreateDirectory($bundle)
    [IO.File]::Copy($publicKey, $pem, $true)
    $platform = if ($ClientKind -eq 'LinuxStreaming') { 'linux-x64' } else { 'windows-x64' }
    $profileName = if ($Developer) { 'developer' } else { 'general' }
    Write-Json (Join-Path $bundle 'setup.json') @{
        serverUrl = $Lab.origin; projectId = $Project; environment = $Environment; channel = $Channel
        platform = $platform; clientKind = $ClientKind; signingKeyId = 'release-1'; credentialName = $CredentialName
        launcherEdition = if ($Developer) { 'Developer' } else { 'General' }
    }
    $template = Join-Path $bundle ('config-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        if ($ClientKind -eq 'WindowsStreaming') {
            Invoke-Checked $Lab.launcher @('sample-config','--mode','managed-client','--project-id',$Project,'--platform',$platform,
                '--environment',$Environment,'--channel',$Channel,'--output',$template) | Out-Null
        } else {
            Invoke-Checked $Lab.launcher @('sample-config','--server-url',$Lab.origin,'--project-id',$Project,'--platform',$platform,
                '--deployment-mode','portable','--profile',$profileName,'--credential-name',$CredentialName,'--signing-key-id','release-1',
                '--public-key','release-public.pem','--output',$template) | Out-Null
        }
        $settings = Read-Json $template
        $settings.environment = $Environment
        $settings.channel = $Channel
        Write-Json (Join-Path $bundle 'launcher.config.json') $settings
    } finally { [IO.File]::Delete($template) }
    Write-Output "Client bundle (public files only): $bundle"
}

function Show-Publish($Lab) {
    if ($Zip) {
        if (!$Project -or !$Version -or !$PayloadRoot -or !$EntryPoint) {
            throw 'Provide -Project, -Version, -PayloadRoot and -EntryPoint with -Zip.'
        }
        $zipFile = (Resolve-Path -LiteralPath $Zip).ProviderPath
        $id = [guid]::NewGuid().ToString('N')
        $stage = Join-Path $store "drop-$id"
        $drop = Join-Path $incoming $id
        [void][IO.Directory]::CreateDirectory($stage)
        $payload = Join-Path $stage 'payload.zip'
        $sidecar = Join-Path $stage 'release.json'
        try {
            [IO.File]::Copy($zipFile, $payload)
            $options = @('release-metadata','--zip',$payload,'--project-id',$Project,'--version',$Version,
                '--environment',$Environment,'--channel',$Channel,'--platform',$Platform,'--payload-root',$PayloadRoot,
                '--entry-point',$EntryPoint,'--output',$sidecar)
            if ($ExecutablePaths) { $options += @('--executable-paths',$ExecutablePaths) }
            Invoke-Checked $Lab.launcher $options | Out-Null
            [IO.Directory]::Move($stage, $drop)
        } finally {
            if ([IO.Directory]::Exists($stage)) {
                [IO.File]::Delete($sidecar)
                [IO.File]::Delete($payload)
                [IO.Directory]::Delete($stage)
            }
        }
        Write-Output "Placed ZIP and release.json: $drop"
    }
    Write-Output "Manual drop: $incoming\<unique-id>\ with the ZIP and its external release.json."
    Write-Output "Inspect, approve, then promote in: & '$($Lab.server.Replace("'","''"))' console --config '$($config.Replace("'","''"))'"
}

Assert-NoLink $Root
$newLab = $null
if (!(Test-Path -LiteralPath $Root)) {
    if ($Action -in 'Status','Stop') { Write-Output 'Not configured'; return }
    if ($Action -ne 'Start') { throw 'Run Start first.' }
    $ip = Get-IPv4 $Address
    if ($Port -eq $BackendPort) { throw 'Use distinct proxy and backend ports.' }
    $server = (Resolve-Path -LiteralPath $ServerExe).ProviderPath
    $launcher = (Resolve-Path -LiteralPath $LauncherExe).ProviderPath
    Assert-NoLink $server
    Assert-NoLink $launcher
    if ([IO.Path]::GetFileName($server) -ne 'UeDtLauncher.DistributionServer.exe' -or
        [IO.Path]::GetFileName($launcher) -ne 'UeDtLauncher.exe') {
        throw 'Specify the published win-x64 server and general launcher executables.'
    }
    $archive = (Resolve-Path -LiteralPath $CaddyZip).ProviderPath
    Assert-Hash $archive $archiveHash
    Assert-PortFree $ip $Port
    Assert-PortFree ([Net.IPAddress]::Loopback) $BackendPort
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($Root))
    if ($drive.DriveType -ne 'Fixed' -or $drive.DriveFormat -ne 'NTFS') { throw 'Use a local fixed NTFS drive.' }
    if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($Root)) -PathType Container)) { throw 'The lab root parent must exist.' }
    # Windows PowerShell 5.1 can apply the private ACL as part of directory creation.
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $uid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($uid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    [void][IO.Directory]::CreateDirectory($Root, $acl)
    $newLab = @{ schema=1; server=$server; launcher=$launcher; origin="http://$($ip):$Port"; backend="http://127.0.0.1:$BackendPort" }
} elseif (!(Test-Path -LiteralPath $marker -PathType Leaf)) {
    throw 'This directory has no completed lab setup. Use a fresh root and inspect the old directory manually.'
}

$lockPath = Join-Path $Root 'lab.lock'
Assert-NoLink $lockPath
$lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
$serverProcess = $proxyProcess = $null
try {
    if ($newLab) {
        if (@(Get-ChildItem -LiteralPath $Root -Force | Where-Object Name -ne 'lab.lock').Count) { throw 'Use a new lab directory.' }
        Initialize-Lab $newLab $archive
    }
    $lab = Read-Json $marker
    if ($lab.schema -ne 1) { throw 'Unsupported lab metadata.' }
    if (Test-Path -LiteralPath $journal) {
        $records = Read-Json $journal
        $serverProcess = Get-OwnedProcess $records.server $lab.server
        $proxyProcess = Get-OwnedProcess $records.proxy $caddy
    }
    switch ($Action) {
        'Start' {
            Assert-Hash $caddy $caddyHash
            if ($CaddyZip) { Assert-Hash (Resolve-Path -LiteralPath $CaddyZip).ProviderPath $archiveHash }
            if (($Address -and $Address -ne ([uri]$lab.origin).Host) -or
                ($PSBoundParameters.ContainsKey('Port') -and $Port -ne ([uri]$lab.origin).Port) -or
                ($PSBoundParameters.ContainsKey('BackendPort') -and $BackendPort -ne ([uri]$lab.backend).Port) -or
                ($ServerExe -and (Resolve-Path -LiteralPath $ServerExe).ProviderPath -ne $lab.server) -or
                ($LauncherExe -and (Resolve-Path -LiteralPath $LauncherExe).ProviderPath -ne $lab.launcher)) {
                throw 'Lab inputs differ from the saved identity. Use a new root for another origin or executable.'
            }
            if ($serverProcess -and $proxyProcess) {
                Wait-Endpoints $lab.origin $proxyProcess
                Show-Lab $lab
                Write-Output 'Already running'
                break
            }
            if ($serverProcess -or $proxyProcess) { throw 'Partial run detected. Run Stop before Start.' }
            Assert-PortFree (Get-IPv4 ([uri]$lab.origin).Host) ([uri]$lab.origin).Port
            Assert-PortFree ([Net.IPAddress]::Loopback) ([uri]$lab.backend).Port
            $records = @{ server=$null; proxy=$null }
            try {
                $serverProcess = Start-LabProcess $lab.server ('serve --config "' + $config + '"') 'server'
                $records.server = Get-ProcessRecord $serverProcess
                Write-Json $journal $records
                Wait-Endpoints $lab.backend $serverProcess
                $proxyProcess = Start-LabProcess $caddy ('run --config "' + $caddyfile + '" --adapter caddyfile') 'caddy'
                $records.proxy = Get-ProcessRecord $proxyProcess
                Write-Json $journal $records
                Wait-Endpoints $lab.origin $proxyProcess
            } catch {
                try { Stop-LabProcess $proxyProcess } finally { Stop-LabProcess $serverProcess }
                throw
            }
            Show-Lab $lab
            Write-Output 'Started. Challenge 200 and anonymous Catalog 401 verified through Caddy.'
        }
        'Status' {
            [pscustomobject]@{
                Origin=$lab.origin; Incoming=$incoming
                ServerPid=$(if ($serverProcess) { $serverProcess.Id })
                ProxyPid=$(if ($proxyProcess) { $proxyProcess.Id })
            }
        }
        'Stop' {
            try { Stop-LabProcess $proxyProcess } finally { Stop-LabProcess $serverProcess }
            [IO.File]::Delete($journal)
            Write-Output 'Stopped owned processes. The lab data is available for restart.'
        }
        'Add-Client' { Add-LabClient $lab }
        'Publish' { Show-Publish $lab }
    }
} finally {
    if ($proxyProcess) { $proxyProcess.Dispose() }
    if ($serverProcess) { $serverProcess.Dispose() }
    $lock.Dispose()
}
