namespace UeDtLauncher;

/// <summary>
/// Exclusive file lock that prevents two launcher processes from updating the same installation.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    public static SingleInstanceLock Acquire(string lockPath)
    {
        if (!TryAcquire(lockPath, out var acquired))
        {
            throw new InvalidOperationException("Another launcher instance is already updating this installation. Close it and try again.");
        }

        return acquired!;
    }

    public static bool TryAcquire(string lockPath, out SingleInstanceLock? acquired)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            acquired = new SingleInstanceLock(stream);
            return true;
        }
        catch (IOException)
        {
            acquired = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            acquired = null;
            return false;
        }
    }

    public static string LockPathFor(string installDir) => Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".launcher-lock";

    public void Dispose() => _stream.Dispose();
}
