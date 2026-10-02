namespace UeDtLauncher;

public sealed class LauncherScheduledCheckOptions
{
    public bool Enabled { get; set; }
    public int? IntervalSeconds { get; set; }
}
public sealed record ScheduledCheckResult(string Status, DoctorReport? Readiness, ManagedProjectStatus? Installation, string? FailureCode);

public static class ScheduledChecks
{
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
                    if(doctor.Checks.All(check=>check.State is not ("failed" or "waiting")))
                    {
                        if(config.IsManagedDeployment)
                        {
                            var result=await new ManagedAgentClient().SendStreamingAsync("check",config.ProjectId,_=>{},cancellationToken:token);
                            result.ThrowIfFailed();status=result.ProjectStatus;
                        }
                        else
                        {
                            using var http=SecureHttpClientFactory.Create(config);
                            await CatalogResolver.ResolveAsync(config,http,cancellationToken:token);
                            var manifest=await ManifestDownloader.DownloadAsync(config,http,cancellationToken:token);
                            status=await ManagedProjectStatusInspector.InspectAsync(config,manifest.Manifest,token);
                        }
                    }
                    var report=new ScheduledCheckResult(doctor.Healthy?"checked":"action-required",doctor,status,null);
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
