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
        List<string> candidates;
        using (var gate = store.Lock())
        {
            var referenced = store.List().Select(j => j.Snapshot).OfType<string>()
                .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var active = new List<(string Job, string? Scratch, string? Snapshot)>();
            using (var database = store.DatabaseRead())
            {
                using var db = store.Open(); using var command = db.CreateCommand();
                command.CommandText = "SELECT job,scratch,snapshot FROM active_work";
                using var reader = command.ExecuteReader();
                while (reader.Read()) active.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            foreach (var work in active)
            {
                IDisposable? ownerLock = null;
                try { ownerLock = store.LockJob(work.Job); }
                catch (IOException)
                {
                    // The operating system, not elapsed wall time, establishes that the owner is alive.
                    if (work.Scratch is not null) referenced.Add(work.Scratch);
                    if (work.Snapshot is not null) referenced.Add(work.Snapshot);
                }
                if (ownerLock is not null)
                    using (ownerLock)
                    {
                        if (apply)
                        {
                            using var database = store.DatabaseWrite();
                            using var db = store.Open(); using var command = db.CreateCommand();
                            command.CommandText = "DELETE FROM active_work WHERE job=$job";
                            command.Parameters.AddWithValue("$job", work.Job); command.ExecuteNonQuery();
                        }
                    }
            }
            candidates = Directory.EnumerateDirectories(Path.Combine(store.Root, "processing"))
                .Where(d => !referenced.Contains(d)).ToList();
        }
        // Includes failed scratch extraction and publication build directories, never active releases or retained ZIPs.
        // New workers register unique scratch paths before creating them. Deletion needs no global writer lock.
        if (apply)
            foreach (var path in candidates)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked directories.");
                _ = DistributionBackup.Files(path); // Reject nested links, not just the top-level candidate.
                Directory.Delete(path, true);
            }
        return new { Applied = apply, Directories = candidates };
    }
}
