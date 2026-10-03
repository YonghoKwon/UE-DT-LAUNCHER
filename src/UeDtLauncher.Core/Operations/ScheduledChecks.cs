namespace UeDtLauncher;

public sealed class LauncherScheduledCheckOptions
{
    public bool Enabled { get; set; }
    public int? IntervalSeconds { get; set; }
}
public sealed record ScheduledCheckResult(string Status, DoctorReport? Readiness, ManagedProjectStatus? Installation, string? FailureCode);

public static class ScheduledChecks
{
    internal static string Outcome(DoctorReport doctor,bool inspected)=>doctor.PreparationState switch
    {"action-required"=>"action-required","checks-passed" when inspected=>"checked",_=>"verification-pending"};
    public static async Task<ScheduledCheckResult> RunOnceAsync(string configPath,CancellationToken token=default)
    {
        var config=await LauncherPaths.LoadResolvedAsync(configPath,token,readOnly:true);
        if(!config.ScheduledCheck.Enabled)return new("disabled",null,null,null);
        if(config.ScheduledCheck.IntervalSeconds is null or <=0)throw new InvalidOperationException("Enabled scheduled checks require an explicit positive interval.");
        var path=SafePath.ResolveInsideChecked(config.LogDir,"scheduled-check.lock");
        if(!SingleInstanceLock.TryAcquire(path,out var lease))return new("skipped-busy",null,null,null);
        using(lease)
        {
            for(var attempt=0;attempt<2;attempt++)
            {
                try
                {
                    var doctor=await LauncherDoctor.RunAsync(configPath,true,token);
                    if(attempt==0 && doctor.Checks.Any(check=>check.Code is "server-unavailable" or "service-unavailable"))
                    {await Task.Delay(1000,token);continue;}
                    ManagedProjectStatus? status=null;
                    if(doctor.SelectedRelease is { } selected && doctor.Checks.All(check=>check.State is not ("failed" or "waiting")))
                    {
                        if(config.IsManagedDeployment)
                        {
                            var result=await new ManagedAgentClient().SendStreamingAsync("check",config.ProjectId,_=>{},cancellationToken:token,selection:selected);
                            result.ThrowIfFailed();if(result.SelectedRelease!=selected)throw new InvalidDataException("Scheduled check returned another release.");status=result.ProjectStatus;
                        }
                        else
                        {
                            using var http=SecureHttpClientFactory.Create(config);
                            config.VersionPolicy="exact";config.RequestedVersion=selected.Version;config.Environment=selected.Environment;config.Channel=selected.Channel;
                            await CatalogResolver.ResolveAsync(config,http,cancellationToken:token);
                            if(config.SelectedRelease!=selected)throw new InvalidDataException("Scheduled release changed.");
                            var manifest=await ManifestDownloader.DownloadAsync(config,http,cancellationToken:token);
                            status=await ManagedProjectStatusInspector.InspectAsync(config,manifest.Manifest,token);
                        }
                    }
                    var report=new ScheduledCheckResult(Outcome(doctor,status is not null),doctor,status,null);
                    await JsonFiles.WriteAsync(SafePath.ResolveInsideChecked(config.LogDir,"scheduled-check.json"),report,token);return report;
                }
                catch(Exception error)when(error is not OperationCanceledException)
                {
                    var code=LauncherFailure.Code(error);
                    if(attempt==0 && code is "server-unavailable" or "service-unavailable"){await Task.Delay(1000,token);continue;}
                    var result=new ScheduledCheckResult("failed",null,null,code);
                    await JsonFiles.WriteAsync(SafePath.ResolveInsideChecked(config.LogDir,"scheduled-check.json"),result,token);return result;
                }
            }
        }
        throw new InvalidOperationException("Check failed.");
    }
}
