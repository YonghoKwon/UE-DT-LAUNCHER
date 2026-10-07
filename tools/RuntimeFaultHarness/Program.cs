using System.Text.Json;
using UeDtLauncher;

// Test-only executable, deliberately absent from solution/installer/release publish graphs.
if (args.FirstOrDefault() == "runtime-data-smoke") { await RuntimeDataSmoke.RunAsync(args.Skip(1).ToArray()); return; }
if (args.FirstOrDefault() == "runtime-data-payload") { RuntimeDataSmoke.Payload(args); return; }
var root=Path.GetFullPath(args[0]); var operation=args[1]; var boundary=args[2];
Directory.CreateDirectory(root);
var configPath=Path.Combine(root,"config.json");
var config=new LauncherConfig { ProjectId="demo",InstallDir=Path.Combine(root,"app"),StateRootDir=Path.Combine(root,"state"),LogDir=Path.Combine(root,"logs") };
await JsonFiles.WriteAsync(configPath,config); LauncherPaths.ResolveInPlace(config,configPath);
Directory.CreateDirectory(config.InstallDir); File.WriteAllText(Path.Combine(config.InstallDir,"game"),"fixture");
var actor=RuntimeIdentities.Current();
RuntimeStore.Write(config,new() { InstallationId=RuntimeStore.InstallationId(config),State=RuntimeState.Quiescent,Origin="operator-confirmed",Requester=actor });
await JsonFiles.WriteAsync(config.InstalledManifestPath,new LauncherManifest { AppId="demo",Version="1",Platform=config.TargetPlatform,EntryPoint="game",Files=[new() {Path="game",Size=7,Sha256=new string('a',64)}] });
RuntimeLaunchTicket? ticket=null;
if(operation is "attach" or "started" or "complete") ticket=RuntimeStore.Begin(config,actor,actor.Executable);
if(operation is "started" or "complete") RuntimeStore.Attach(config,ticket!,actor,actor.Executable);
if(operation=="complete") RuntimeStore.Report(config,ticket!,actor,false);
var service=operation.StartsWith("service-",StringComparison.Ordinal);
if(service) RuntimeServiceState.RequireSelection(config,"1");
if(operation is "service-healthy" or "service-failure" or "service-confirm") RuntimeServiceState.Starting(config,"backup");
var target=service?RuntimeServiceState.SnapshotPath(config):RuntimeStore.RecordPath(config);
var hit=0;
RuntimeStatePersistence.Boundary.Value=(path,point)=>
{
    if(path!=target || point!=boundary || ++hit!=(operation=="service-confirm"?2:1)) return;
    Console.WriteLine(JsonSerializer.Serialize(new { ready=true,path,config=configPath,operation,boundary })); Console.Out.Flush();
    _=Console.ReadLine(); throw new InvalidOperationException("Controller must terminate the owned harness at the boundary.");
};
switch(operation)
{
    case "begin": RuntimeStore.Begin(config,actor,actor.Executable); break;
    case "attach": RuntimeStore.Attach(config,ticket!,actor,actor.Executable); break;
    case "started": RuntimeStore.Report(config,ticket!,actor,false); break;
    case "complete": RuntimeStore.Report(config,ticket!,actor,true); break;
    case "recover": RuntimeStore.Recover(config,actor,true); break;
    case "service-starting": RuntimeServiceState.Starting(config,"backup"); break;
    case "service-healthy": RuntimeServiceState.Healthy(config); break;
    case "service-failure": RuntimeServiceState.RecordFailure(config,"backup"); break;
    case "service-confirm": RuntimeServiceState.ConfirmSelection(config,actor,"1"); break;
    default: throw new ArgumentException("Unknown test operation");
}
