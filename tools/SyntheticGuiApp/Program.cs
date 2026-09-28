using System.Diagnostics;
using System.Text.Json;

// Test-only payload: no shell, network, installer, or user data access.
var index=Array.IndexOf(args,"--control-root");
if(index<0 || index+1>=args.Length) return 2;
var root=Path.GetFullPath(args[index+1]); Directory.CreateDirectory(root);
var child=Array.IndexOf(args,"--child");
if(child<0)
{
    var id=Guid.NewGuid().ToString("N");
    var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true };
    foreach(var argument in new[] {"--control-root",root,"--child",id}) start.ArgumentList.Add(argument);
    using var process=Process.Start(start) ?? throw new IOException("Cannot start synthetic child");
    File.WriteAllText(Path.Combine(root,id+".parent.json"),JsonSerializer.Serialize(new {parent=Environment.ProcessId,child=process.Id}));
    return 0;
}
var attempt=args[child+1];
if(!Guid.TryParseExact(attempt,"N",out _)) return 2;
var version=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"version.txt")).Trim();
File.WriteAllText(Path.Combine(root,attempt+".started.json"),JsonSerializer.Serialize(new {id=attempt,pid=Environment.ProcessId,version}));
var elapsed=Stopwatch.StartNew();
while(elapsed.Elapsed<TimeSpan.FromSeconds(120) && !File.Exists(Path.Combine(root,"release-"+attempt))) await Task.Delay(100);
File.WriteAllText(Path.Combine(root,attempt+".ended.json"),JsonSerializer.Serialize(new {id=attempt,version,naturalExit=true}));
return 0;
