namespace UeDtLauncher.Distribution;

public static class StorageMaintenance
{
    public static object Usage(IntakeStore store) => new
    {
        Jobs = store.List().Count,
        Directories = new[] { "incoming", "processing", "archive", "releases" }.Select(name => new
        { Name = name, Bytes = Directory.EnumerateFiles(Path.Combine(store.Root, name), "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) })
    };
    public static object Cleanup(IntakeStore store, bool apply)
    {
        using var gate = store.Lock();
        var referenced = store.List().Select(j => j.Snapshot).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var candidates = Directory.EnumerateDirectories(Path.Combine(store.Root, "processing"))
            .Where(d => !referenced.Contains(d)).ToList();
        // Includes failed scratch extraction and publication build directories, never active releases or retained ZIPs.
        if (apply)
            foreach (var path in candidates)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked directories.");
                Directory.Delete(path, true);
            }
        return new { Applied = apply, Directories = candidates };
    }
}
