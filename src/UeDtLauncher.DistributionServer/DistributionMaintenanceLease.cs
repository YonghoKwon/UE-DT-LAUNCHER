using System.Runtime.InteropServices;

namespace UeDtLauncher.Distribution;

/// <summary>Shared live operations vs exclusive offline maintenance. Never kills or unlinks a lock owner.</summary>
public sealed class DistributionMaintenanceLease : IDisposable
{
    private readonly FileStream stream;
    private DistributionMaintenanceLease(FileStream stream) => this.stream = stream;
    public static DistributionMaintenanceLease Acquire(string root, bool exclusive, bool create = true)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            if (!create) throw new IOException("Server root is unavailable.");
            Directory.CreateDirectory(root);
        }
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Maintenance refuses a linked root.");
        if (exclusive)
        {
            var actor = RuntimeIdentities.Current();
            if (!actor.Administrator && actor.Owner != RuntimeIdentities.DirectoryOwner(root))
                throw new UnauthorizedAccessException("Offline maintenance requires the server owner or an administrator.");
        }
        var path = SafePath.ResolveInsideChecked(root, ".maintenance.lock");
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read,
            OperatingSystem.IsWindows() ? exclusive ? FileShare.None : FileShare.Read : FileShare.ReadWrite);
        if (OperatingSystem.IsLinux() && flock(checked((int)file.SafeFileHandle.DangerousGetHandle()), (exclusive ? 2 : 1) | 4) != 0)
        { file.Dispose(); throw new IOException("Server/workers are active or another maintenance operation owns this root."); }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        { file.Dispose(); throw new PlatformNotSupportedException("Maintenance locking supports Windows/Linux only."); }
        if(!exclusive && File.Exists(SafePath.ResolveInsideChecked(root,"restore-staged.json")))
        {file.Dispose();throw new InvalidDataException("Staged restored root rejects live operations.");}
        return new(file);
    }
    public void Dispose() => stream.Dispose();
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
}
