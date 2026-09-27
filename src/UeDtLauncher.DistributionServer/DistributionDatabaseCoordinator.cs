namespace UeDtLauncher.Distribution;

/// <summary>Process-local coordination of short synchronous SQLite intervals, not package I/O.</summary>
internal static class DistributionDatabaseCoordinator
{
    private static readonly object RegistryLock = new();
    private static readonly Dictionary<string, WeakReference<ReaderWriterLockSlim>> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static ReaderWriterLockSlim ForRoot(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (RegistryLock)
        {
            // Stores/active scopes own gates. A transient test/CLI root must not stay rooted forever.
            foreach (var stale in Gates.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
                Gates.Remove(stale);
            if (Gates.TryGetValue(root, out var weak) && weak.TryGetTarget(out var existing)) return existing;
            var gate = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            Gates[root] = new(gate);
            return gate;
        }
    }

    public static IDisposable Read(ReaderWriterLockSlim gate)
    {
        gate.EnterReadLock();
        return new Scope(gate, write: false);
    }

    public static IDisposable Write(ReaderWriterLockSlim gate)
    {
        gate.EnterWriteLock();
        return new Scope(gate, write: true);
    }

    // ReaderWriterLockSlim is thread-affine. Callers must finish/dispose synchronously before any await.
    private sealed class Scope(ReaderWriterLockSlim gate, bool write) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            if (write) gate.ExitWriteLock(); else gate.ExitReadLock();
            disposed = true;
        }
    }
}
