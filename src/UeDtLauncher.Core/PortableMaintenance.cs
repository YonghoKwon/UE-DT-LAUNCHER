namespace UeDtLauncher;

public static class PortableMaintenance
{
    public static void ClearStaging(LauncherConfig config,Action<string>? log=null)
    {
        using var lease=Acquire(config);
        var path=Checked(config,config.StagingDir);
        RejectLinks(path);
        if(Directory.Exists(path))Directory.Delete(path,true);
        log?.Invoke("임시 파일을 정리했습니다. 이어받기 기록은 보존됩니다.");
    }
    public static void PruneBackups(LauncherConfig config,Action<string>? log=null)
    {
        using var lease=Acquire(config);
        var path=Checked(config,config.BackupDir);RejectLinks(path);
        BackupManager.Prune(path,config.MaxBackupCount,log);
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
    private static string Checked(LauncherConfig config,string path)
    {
        var root=Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))!;
        return SafePath.ResolveInsideChecked(root,Path.GetRelativePath(root,Path.GetFullPath(path)));
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
