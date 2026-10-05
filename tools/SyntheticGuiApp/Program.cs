using System.Diagnostics;
using System.Text.Json;

// Test-only payload: no shell, network, installer, or user data access.
var index=Array.IndexOf(args,"--control-root");
if(index<0 || index+1>=args.Length) return 2;
var root=Path.GetFullPath(args[index+1]); Directory.CreateDirectory(root);
var version=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"version.txt")).Trim();
var platform=OperatingSystem.IsWindows()?"windows-x64":"linux-x64";
var runtimePath=Path.Combine(Path.GetDirectoryName(root)!,"client","state","demo","prod","stable",version,platform,"runtime-state.json");
var smoke=args.Contains("--smoke",StringComparer.Ordinal);
var watchdogMs=120000;
var watchdogIndex=Array.IndexOf(args,"--watchdog-ms");
if(watchdogIndex>=0 && (!smoke || watchdogIndex+1>=args.Length || !int.TryParse(args[watchdogIndex+1],out watchdogMs) || watchdogMs is <0 or >120000))return 2;
string? runtimeAttempt=null;
if(!smoke)
{
    using var runtime=JsonDocument.Parse(File.ReadAllText(runtimePath));
    runtimeAttempt=runtime.RootElement.GetProperty("attemptId").GetString();
    if(!Guid.TryParseExact(runtimeAttempt,"N",out _))return 2;
    var entry=runtime.RootElement.GetProperty("entryPoint").GetString();
    if(!string.Equals(Path.GetFullPath(entry!),Path.GetFullPath(Environment.ProcessPath!),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return 2;
}
var child=Array.IndexOf(args,"--child");
if(child<0)
{
    var id=Guid.NewGuid().ToString("N");
    var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
    foreach(var argument in new[] {"--control-root",root,"--child",id}) start.ArgumentList.Add(argument);
    if(smoke)start.ArgumentList.Add("--smoke");
    if(watchdogIndex>=0){start.ArgumentList.Add("--watchdog-ms");start.ArgumentList.Add(watchdogMs.ToString());}
    using var process=Process.Start(start) ?? throw new IOException("Cannot start synthetic child");
    File.WriteAllText(Path.Combine(root,id+".parent.json"),JsonSerializer.Serialize(new {parent=Environment.ProcessId,child=process.Id,runtimeAttemptId=runtimeAttempt}));
    return 0;
}
var attempt=args[child+1];
if(!Guid.TryParseExact(attempt,"N",out _)) return 2;
File.WriteAllText(Path.Combine(root,attempt+".started.json"),JsonSerializer.Serialize(new {id=attempt,pid=Environment.ProcessId,version,runtimeAttemptId=runtimeAttempt,startedAtUtc=DateTimeOffset.UtcNow,watchdogSeconds=120}));
var elapsed=Stopwatch.StartNew();
var reason="watchdog";
while(elapsed.Elapsed<TimeSpan.FromMilliseconds(watchdogMs))
{
    if(File.Exists(Path.Combine(root,"release-"+attempt))){reason="requested";break;}
    await Task.Delay(100);
}
File.WriteAllText(Path.Combine(root,attempt+".ended.json"),JsonSerializer.Serialize(new {id=attempt,version,naturalExit=true,reason,endedAtUtc=DateTimeOffset.UtcNow}));
return 0;
