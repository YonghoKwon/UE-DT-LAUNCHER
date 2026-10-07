param(
    [Parameter(Mandatory=$true)][string]$Msi,
    [Parameter(Mandatory=$true)][string]$Gui,
    [Parameter(Mandatory=$true)][string]$Agent,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Developer
)
$ErrorActionPreference = 'Stop'
foreach ($file in @($Msi,$Gui,$Agent)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing payload verification input: $file" } }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Extraction requires a new empty output directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
if (-not ('UeDt.MsiStreams' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
namespace UeDt {
 public static class MsiStreams {
  [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persistence, out uint database);
  [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
  [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view, uint record);
  [DllImport("msi.dll")] static extern uint MsiViewFetch(uint view, out uint record);
  [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiRecordGetString(uint record, uint field, StringBuilder buffer, ref uint length);
  [DllImport("msi.dll")] static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint length);
  [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
  static void Check(uint code) { if (code != 0) throw new IOException("Read-only MSI operation failed: " + code); }
  public static string[] ExtractCabinets(string path, string output) {
   uint db=0, view=0, record=0; var files=new System.Collections.Generic.List<string>();
   try {
    Check(MsiOpenDatabase(path, IntPtr.Zero, out db));
    Check(MsiDatabaseOpenView(db, "SELECT `Name`, `Data` FROM `_Streams`", out view)); Check(MsiViewExecute(view,0));
    uint result;
    while ((result=MsiViewFetch(view,out record)) == 0) {
     try {
      uint size=1024; var name=new StringBuilder(1025); Check(MsiRecordGetString(record,1,name,ref size));
      var value=name.ToString(); if (!value.EndsWith(".cab",StringComparison.OrdinalIgnoreCase)) continue;
      if (Path.GetFileName(value)!=value || value.Contains("..") || value.Contains(":")) throw new IOException("Unsafe cabinet name.");
      var target=Path.Combine(output,value); using(var stream=new FileStream(target,FileMode.CreateNew)) {
       var buffer=new byte[65536]; do { size=(uint)buffer.Length; Check(MsiRecordReadStream(record,2,buffer,ref size)); stream.Write(buffer,0,(int)size); } while(size!=0);
      } files.Add(target);
     } finally { MsiCloseHandle(record); record=0; }
    }
    if(result!=259) Check(result); if(files.Count==0) throw new IOException("Embedded cabinet missing."); return files.ToArray();
   } finally { if(record!=0) MsiCloseHandle(record); if(view!=0) MsiCloseHandle(view); if(db!=0) MsiCloseHandle(db); }
  }
 }
}
'@
}
$cabinets = [UeDt.MsiStreams]::ExtractCabinets([IO.Path]::GetFullPath($Msi), [IO.Path]::GetFullPath($OutputDirectory))
$extracted = @{}
$ids=@('LauncherGuiExe','LauncherAgentExe'); if($Developer){$ids+='LauncherDeveloperExe'}
foreach ($id in $ids) {
    foreach ($cabinet in $cabinets) {
        & "$env:SystemRoot\System32\expand.exe" "-F:$id" $cabinet $OutputDirectory | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Cabinet extraction failed.' }
    }
    $path = Join-Path $OutputDirectory $id
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "MSI payload is missing $id" }
    $extracted[$id] = $path
}
$hashes = @{}
$pairs=@(@($Gui,'LauncherGuiExe'), @($Agent,'LauncherAgentExe')); if($Developer){$pairs+=,@($Developer,'LauncherDeveloperExe')}
foreach ($pair in $pairs) {
    $expected = (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath $extracted[$pair[1]] -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "MSI embedded payload differs: $($pair[1])" }
    $hashes[$pair[1]] = $actual.ToLowerInvariant()
}
[pscustomobject]@{ Gui=$extracted.LauncherGuiExe; Agent=$extracted.LauncherAgentExe; Developer=$extracted['LauncherDeveloperExe']; Hashes=$hashes }
