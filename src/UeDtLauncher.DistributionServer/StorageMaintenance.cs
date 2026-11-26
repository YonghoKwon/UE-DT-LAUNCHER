using UeDtLauncher.Distribution.Authority;

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
        List<string> candidates = [];
        using (var writer = store.LockWriter())
        {
            if (apply) store.Authority.CommitUnderHeldLock(writer, Plan);
            else Plan(store.Authority.ReadSnapshot());

            CommitPlan Plan(AuthoritySnapshot snapshot)
            {
                var jobs = IntakeJobs.Read(snapshot);
                var referenced = jobs.Jobs.Select(job => job.Snapshot).OfType<string>()
                    .Select(path => IntakeJobs.Resolve(store.Root, path)).ToHashSet(IntakeJobs.PathComparer);
                var stale = new HashSet<string>(StringComparer.Ordinal);
                foreach (var work in jobs.ActiveWork)
                {
                    try
                    {
                        using var ownerLock = store.LockJob(work.Job);
                        stale.Add(work.Job);
                    }
                    catch (IOException)
                    {
                        // LockJob attempts once. The writer must never wait for a live job owner.
                        if (work.Scratch is not null) referenced.Add(IntakeJobs.Resolve(store.Root, work.Scratch));
                        if (work.Snapshot is not null) referenced.Add(IntakeJobs.Resolve(store.Root, work.Snapshot));
                    }
                }
                candidates = Directory.EnumerateDirectories(Path.Combine(store.Root, "processing"))
                    .Where(path => !referenced.Contains(path)).ToList();
                return apply && jobs.ActiveWork.RemoveAll(work => stale.Contains(work.Job)) > 0
                    ? jobs.Plan() : CommitPlan.Audit();
            }
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
