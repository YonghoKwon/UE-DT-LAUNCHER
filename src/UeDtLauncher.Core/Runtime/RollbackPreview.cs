using System.Security.Cryptography;
using System.Text;
namespace UeDtLauncher;

public sealed record RollbackPreview(string BackupId,string MetadataFingerprint,string CreatedAtUtc,string? CurrentVersion,string? RestoreVersion,bool RestoresUninstalledState,bool CanRestore);
public sealed class RollbackPreviewChangedException() : InvalidOperationException("Backup preview changed. Refresh the preview and confirm again.");
public static class RollbackPreviewService
{
    public const string Capability="rollback-preview-v1";
    public static async Task<RollbackPreview?> ReadAsync(LauncherConfig config,CancellationToken token=default)
    {
        if(File.Exists(UpdateTransactionManager.JournalPath(config))) throw new InvalidOperationException("Backup is changing; check again after the installation operation.");
        var latest=BackupManager.List(config.BackupDir).FirstOrDefault();
        if(latest.BackupRoot is null)return null;
        var id=Path.GetFileName(latest.BackupRoot);
        var meta=SafePath.ResolveInsideChecked(config.BackupDir,id+"/"+BackupManager.MetaDirName);
        var paths=new[]{Path.Combine(meta,"backup-info.json"),Path.Combine(meta,"installed-manifest.json"),Path.Combine(meta,"install-state.json"),config.InstalledManifestPath};
        var hashes=new List<string> {RuntimeStore.InstallationId(config),id};
        foreach(var path in paths)hashes.Add(File.Exists(path)?await Hashing.Sha256FileAsync(path,token):"missing");
        var before=File.Exists(paths[1])?await JsonFiles.ReadAsync<LauncherManifest>(paths[1],token):null;
        var current=File.Exists(config.InstalledManifestPath)?await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath,token):null;
        var uninstalled=before is null&&!File.Exists(paths[2])&&latest.Info?.PreviousVersion is null&&latest.Info?.AddedPaths.Count>0;
        var valid=latest.Info is not null&&(before is not null||uninstalled);
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',hashes))));
        return new(id,fingerprint,latest.Info?.CreatedAtUtc??"",current?.Version,before?.Version,uninstalled,valid);
    }
    public static async Task RestoreExpectedAsync(LauncherConfig config,string expectedId,string expectedFingerprint,Action<string>? log=null,CancellationToken token=default)
    {
        using var lease=InstallationMutationLease.Acquire(config);
        var preview=await ReadAsync(config,token);
        if(preview is null||!preview.CanRestore||preview.BackupId!=expectedId||preview.MetadataFingerprint!=expectedFingerprint)
            throw new RollbackPreviewChangedException();
        await BackupManager.RestoreUnderLeaseAsync(SafePath.ResolveInsideChecked(config.BackupDir,preview.BackupId),config,lease,log,token);
    }
}
