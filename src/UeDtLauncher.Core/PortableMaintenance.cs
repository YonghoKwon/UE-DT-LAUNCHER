namespace UeDtLauncher;
public sealed record PortableMaintenanceResult(int RemovedCount,int FailedCount,int RemainingCount,bool ResumeRecordsPreserved=true);

public static class PortableMaintenance
{
    public static void ClearStaging(LauncherConfig config,Action<string>? log=null)
        =>ClearStagingWithResult(config,log);
    public static PortableMaintenanceResult ClearStagingWithResult(LauncherConfig config,Action<string>? log=null)
    {
        using var lease=Acquire(config);
        var path=Checked(config,config.StagingDir,"staging");
        RejectLinks(path);
        var cache=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))!,"resume-cache");
        var before=CacheInventory(cache);var existed=Directory.Exists(path);
        try{if(existed)Directory.Delete(path,true);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
        {log?.Invoke("임시 파일 일부를 정리하지 못했습니다: "+ex.Message);return new(0,1,Directory.Exists(path)?1:0,before.SequenceEqual(CacheInventory(cache)));}
        log?.Invoke("임시 파일을 정리했습니다. 이어받기 기록은 보존됩니다.");
        return new(existed?1:0,0,Directory.Exists(path)?1:0,before.SequenceEqual(CacheInventory(cache)));
    }
    public static void PruneBackups(LauncherConfig config,Action<string>? log=null)
        =>PruneBackupsWithResult(config,log);
    public static PortableMaintenanceResult PruneBackupsWithResult(LauncherConfig config,Action<string>? log=null)
    {
        using var lease=Acquire(config);
        var path=Checked(config,config.BackupDir,"backups");RejectLinks(path);
        var result=BackupManager.PruneWithResult(path,config.MaxBackupCount,log);
        return new(result.RemovedCount,result.FailedCount,result.RemainingCount);
    }
    private static string[] CacheInventory(string path)
    {
        if(!Directory.Exists(path))return [];
        RejectLinks(path);
        var entries=Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories).Take(100001)
            .Select(p=>{var f=new FileInfo(p);return Path.GetRelativePath(path,p)+":"+f.Length+":"+f.LastWriteTimeUtc.Ticks;})
            .Order(StringComparer.Ordinal).ToArray();
        if(entries.Length>100000)throw new IOException("Too many resume cache entries to verify preservation.");
        return entries;
    }
    private static InstallationMutationLease Acquire(LauncherConfig config)
    {
        if(config.IsManagedDeployment)throw new InvalidOperationException("관리형 정리는 업데이트 서비스의 별도 관리 기능이 필요합니다.");
        var lease=InstallationMutationLease.AcquireMaintenance(config);
        try
        {
            if(File.Exists(UpdateTransactionManager.JournalPath(config)))throw new InvalidOperationException("미완료 설치 작업이 있어 정리할 수 없습니다. 상태를 먼저 확인해 주세요.");
            return lease;
        }
        catch{lease.Dispose();throw;}
    }
    private static string Checked(LauncherConfig config,string path,string directory)
    {
        var root=Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))!;
        var candidate=SafePath.ResolveInsideChecked(root,Path.GetRelativePath(root,Path.GetFullPath(path)));
        if(!string.Equals(candidate,Path.Combine(root,directory),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidDataException("Maintenance must use the selected installation's dedicated directory.");
        return candidate;
    }
    private static void RejectLinks(string path)
    {
        if(!Directory.Exists(path))return;
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked maintenance paths are not allowed.");
        foreach(var item in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes=File.GetAttributes(item);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked maintenance entries are not allowed.");
            if((attributes&FileAttributes.Directory)!=0)RejectLinks(item);
        }
    }
    public static string ExistingFolder(string path)
    {
        var full=Path.GetFullPath(path);
        if(!Directory.Exists(full))throw new DirectoryNotFoundException("설치 폴더가 없습니다. 설치 상태를 먼저 확인해 주세요.");
        return full;
    }
}
