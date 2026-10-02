param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
$fixture = [IO.Path]::GetFullPath($Root)
$record = Get-Content -LiteralPath (Join-Path $fixture 'versioned-real-fixture.json') -Raw | ConvertFrom-Json
if ($record.schemaVersion -ne 2 -or $record.kind -ne 'same-ue-payload-file-delta') { throw 'Not a versioned test fixture' }
$target = [IO.Path]::GetFullPath((Join-Path $fixture 'client/runtime-data'))
if (-not $target.StartsWith($fixture.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Target outside fixture' }
if (Test-Path -LiteralPath $target) { throw 'Existing data permissions are never changed by this helper' }
for ($p = [IO.DirectoryInfo]::new($target); $null -ne $p; $p = $p.Parent) {
    if ($p.Exists -and ($p.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked path rejected' }
}
# Only a new, explicitly named test data directory; no recursive chmod/chown/ACL changes.
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($sid)
foreach ($identity in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($target), $acl)
Write-Output 'Provisioned new isolated runtime data directory; existing data unchanged.'
