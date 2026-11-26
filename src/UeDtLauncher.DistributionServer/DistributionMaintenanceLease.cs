using System.Runtime.InteropServices;

namespace UeDtLauncher.Distribution;

/// <summary>Shared live operations vs exclusive offline maintenance. Never kills or unlinks a lock owner.</summary>
public sealed class DistributionMaintenanceLease : IDisposable
{
    private readonly FileStream stream;
    private readonly string root;
    private readonly bool exclusive;
    private readonly object lifetime = new();

    private DistributionMaintenanceLease(FileStream stream, string root, bool exclusive)
    {
        this.stream = stream;
        this.root = root;
        this.exclusive = exclusive;
    }
    public static DistributionMaintenanceLease Acquire(string root, bool exclusive, bool create = true)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
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
        return new(file, root, exclusive);
    }

    internal T UseExclusive<T>(string expectedRoot, Func<T> read)
    {
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(!stream.CanRead, this);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!exclusive || !root.Equals(expectedRoot, comparison))
                throw new ArgumentException("Read-only authority inspection requires the exclusive maintenance lease for this root.");
            return read();
        }
    }

    public void Dispose()
    {
        lock (lifetime) stream.Dispose();
    }
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
}
