using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

public sealed record RetentionDirectory(string Path,string Identity);
public sealed record RetentionCandidate(string Path, string? JobId, long Bytes, IReadOnlyList<BackupFile> Files,IReadOnlyList<RetentionDirectory>? Directories=null);
public sealed record RetentionPlan(int SchemaVersion, string Root, IReadOnlyList<string> Jobs, IReadOnlyList<string> Temporary,
    string ReferenceHash, IReadOnlyList<RetentionCandidate> Candidates, string Fingerprint, string PlanId);
public sealed record RetentionJournal(RetentionPlan Plan, int NextIndex, string Phase,List<RetentionItem>? Items=null);
public sealed record RetentionItem(int Index,string Phase,string DirectoryIdentity,string Quarantine);
public sealed record RetentionListingCandidate(string Key, string? JobId, string? Temporary, long Bytes);

public static class RetentionMaintenance
{
    internal static readonly AsyncLocal<Action<string>?> Boundary = new(); // Test seam only, never CLI/environment input.
    private static StringComparison Comparison => OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonFiles.Options))).ToLowerInvariant();
    private static List<RetentionDirectory> Directories(string root)
    {
        var result=new List<RetentionDirectory>();
        void Visit(string path,int depth)
        {
            if(depth>128 || result.Count>=500000)throw new IOException("Cleanup directory inventory exceeds maintenance limits.");
            var relative=Path.GetRelativePath(root,path).Replace('\\','/');
            _=SafePath.ResolveInsideChecked(root,relative);
            result.Add(new(relative,MaintenanceStorage.DirectoryIdentity(path)));
            foreach(var child in Directory.EnumerateDirectories(path).Order(StringComparer.Ordinal))Visit(child,depth+1);
        }
        Visit(root,0);return result.OrderBy(d=>d.Path,StringComparer.Ordinal).ToList();
    }
    private static void DeleteApprovedEmptyDirectories(string root,IReadOnlyList<RetentionDirectory> approved)
    {
        var expected=approved.ToDictionary(d=>d.Path,StringComparer.Ordinal);
        foreach(var current in Directories(root))
            if(!expected.TryGetValue(current.Path,out var directory)||current.Identity!=directory.Identity)
                throw new InvalidDataException("Unapproved cleanup directory remains; quarantine and journal are preserved for review.");
        foreach(var directory in approved.OrderByDescending(d=>d.Path.Length))
        {
            var path=directory.Path=="."?root:SafePath.ResolveInsideChecked(root,directory.Path);
            if(!Directory.Exists(path))continue;
            if(MaintenanceStorage.DirectoryIdentity(path)!=directory.Identity || Directory.EnumerateFileSystemEntries(path).Any())
                throw new InvalidDataException("Unapproved cleanup content remains; quarantine and journal are preserved for review.");
            Directory.Delete(path,false);
        }
    }
    private sealed record RetentionReferences(List<(string Id, string Source, string State, string? Snapshot)> Jobs, HashSet<string> Referenced, string Hash);
    private static RetentionReferences References(string root)
    {
        var references = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var jobs = new List<(string, string, string, string?)>(); var proof = new List<string>();
        var snapshot = AuthorityStore.InspectExisting(root, validateHistory: false);
        var intake = IntakeJobs.Read(snapshot);
        var catalog = new CatalogAuthority(snapshot.Store, root).Snapshot(snapshot);
        foreach (var entry in intake.Jobs.OrderBy(job => job.Id, StringComparer.Ordinal))
        {
            var job = (entry.Id, IntakeJobs.Resolve(root, entry.Source), entry.State,
                IntakeJobs.ResolveOptional(root, entry.Snapshot)); jobs.Add(job);
            proof.Add(Hash(new{Id=job.Item1,Source=job.Item2,State=job.Item3,Snapshot=job.Item4}));
            if (job.Item3 is not ("failed" or "rejected")) { references.Add(job.Item2); if (job.Item4 is not null) references.Add(job.Item4); }
        }
        foreach (var release in catalog.Releases.OrderBy(release => release.ReleaseId, StringComparer.Ordinal))
        {
            references.Add(release.Directory);
            proof.Add(Hash(new { release.ReleaseId, release.Directory }));
        }
        foreach (var promotion in catalog.Events)
            proof.Add(Hash(promotion));
        foreach (var work in intake.ActiveWork.OrderBy(work => work.Job, StringComparer.Ordinal))
        {
            var values = new[] { IntakeJobs.ResolveOptional(root, work.Scratch) ?? "",
                IntakeJobs.ResolveOptional(root, work.Snapshot) ?? "" };
            proof.Add(Hash(new { Id = work.Job, Values = values }));
            foreach (var path in values.Where(Path.IsPathRooted)) references.Add(path);
        }
        return new(jobs, references, Hash(proof));
    }
    public static object Inspect(DistributionSettings settings)
    {
        var root = Path.GetFullPath(settings.Root); var current = References(root);
        return new { Jobs = current.Jobs.Select(j => new { j.Id, j.State, Protection = j.State is "failed" or "rejected" ? "explicit selection required" : "published/pending/active history protected" }),
            PublicDeletionAllowed = false, PromotionHistoryProtected = true, ReferenceHash = current.Hash };
    }
    private static List<(string Path, string? JobId)> SelectTargets(string root, RetentionReferences current,
        IReadOnlyList<string> selectedJobs, IReadOnlyList<string> temporary)
    {
        var targets = new List<(string Path, string? JobId)>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string path, string? jobId)
        {
            path = Path.GetFullPath(path); var relative = Path.GetRelativePath(root, path);
            _ = SafePath.ResolveInsideChecked(root, relative);
            if (!(relative.StartsWith("incoming" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("processing" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("archive" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidDataException("Only nonpublic failed/rejected uploads or unreferenced processing children are eligible.");
            if (!paths.Add(path) || current.Referenced.Any(r => string.Equals(path,r,Comparison) || r.StartsWith(path + Path.DirectorySeparatorChar, Comparison) || path.StartsWith(r + Path.DirectorySeparatorChar, Comparison)))
                throw new InvalidDataException("Cleanup target is referenced or duplicated.");
            if (current.Jobs.Any(j => j.Id != jobId && (j.Source == path || j.Snapshot == path))) throw new InvalidDataException("Another job references this target.");
            if (!Directory.Exists(path)) return;
            targets.Add((path, jobId));
        }
        foreach (var id in selectedJobs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var job = current.Jobs.SingleOrDefault(j => j.Id == id);
            if (job.Id is null || job.State is not ("failed" or "rejected")) throw new InvalidDataException("Selected job is missing or protected.");
            if (job.Snapshot is not null) Add(job.Snapshot, id);
            Add(job.Source, id);
        }
        foreach (var path in temporary.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (Path.GetDirectoryName(path.Replace('/',Path.DirectorySeparatorChar)) != "processing") throw new InvalidDataException("Select immediate processing child directories only.");
            Add(SafePath.ResolveInsideChecked(root,path), null);
        }
        return targets;
    }

    /// <summary>Read-only eligibility and size inventory. No file content hashes, plan IDs, journals or DB writes.</summary>
    public static IReadOnlyList<RetentionListingCandidate> InspectCandidates(DistributionSettings settings)
    {
        var root = Path.GetFullPath(settings.Root);
        var current = References(root);
        var result = new List<RetentionListingCandidate>();
        void Add(string key, string? jobId, string? temporary)
        {
            try
            {
                var targets = SelectTargets(root, current, jobId is null ? [] : [jobId], temporary is null ? [] : [temporary]);
                long bytes = 0;
                foreach (var target in targets)
                {
                    foreach (var file in DistributionBackup.Files(target.Path)) bytes = checked(bytes + new FileInfo(file).Length);
                    _ = Directories(target.Path); // Preserve linked/depth/directory-identity rejection without reading payload bytes.
                }
                if (targets.Count > 0) result.Add(new(key, jobId, temporary, bytes));
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var job in current.Jobs.Where(j => j.State is "failed" or "rejected")) Add("job:" + job.Id, job.Id, null);
        foreach (var directory in Directory.EnumerateDirectories(SafePath.ResolveInsideChecked(root, "processing")))
        {
            var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            Add("temp:" + relative, null, relative);
        }
        return result;
    }

    public static async Task<RetentionPlan> PlanAsync(DistributionSettings settings, IReadOnlyList<string> selectedJobs, IReadOnlyList<string> temporary)
    {
        var root = Path.GetFullPath(settings.Root); var current = References(root);
        var candidates = new List<RetentionCandidate>();
        foreach (var (path, jobId) in SelectTargets(root, current, selectedJobs, temporary))
        {
            var files = new List<BackupFile>();
            foreach (var file in DistributionBackup.Files(path)) files.Add(new(Path.GetRelativePath(path, file).Replace('\\', '/'), new FileInfo(file).Length, await Hashing.Sha256FileAsync(file)));
            candidates.Add(new(Path.GetRelativePath(root, path).Replace('\\', '/'), jobId, files.Sum(f => f.Bytes), files, Directories(path)));
        }
        var plan = new RetentionPlan(2, root, selectedJobs.Order(StringComparer.Ordinal).ToArray(), temporary.Order(StringComparer.Ordinal).ToArray(), current.Hash, candidates, "", Guid.NewGuid().ToString("N"));
        return plan with { Fingerprint = Hash(plan) };
    }

    private static DistributionMaintenanceLease ApplyMaintenanceLease(string root)
    {
        try { return DistributionMaintenanceLease.Acquire(root, true, create: false); }
        catch (IOException error) { error.Data["RetentionBeforeMutation"] = true; throw; }
    }
    private static AuthenticationProcessLease ApplyAuthenticationLease(string root)
    {
        try { return new AuthenticationProcessLease(root); }
        catch (IOException error) { error.Data["RetentionBeforeMutation"] = true; throw; }
    }
    public static async Task<object> ApplyAsync(DistributionSettings settings, RetentionPlan plan, bool confirm)
    {
        if(plan.SchemaVersion!=2 || plan.Candidates.Any(c=>c.Directories is null))
            throw new InvalidDataException("Cleanup requires a new schema 2 plan with approved directory identities; preserve old journals for review.");
        if (!confirm || !Guid.TryParseExact(plan.PlanId,"N",out _) || Path.GetFullPath(settings.Root) != plan.Root || Hash(plan with { Fingerprint = "" }) != plan.Fingerprint)
            throw new InvalidDataException("Valid plan and explicit --confirm are required.");
        _ = AuthorityStore.InspectExisting(settings.Root, validateHistory: false);
        using var maintenance = ApplyMaintenanceLease(settings.Root);
        var authority = AuthorityStore.InspectExisting(settings.Root).Store;
        using var oldServer = ApplyAuthenticationLease(settings.Root);
        var journalPath = SafePath.ResolveInsideChecked(settings.Root,"retention-" + plan.Fingerprint + ".json");
        RetentionJournal journal;
        if (File.Exists(journalPath))
        {
            journal = await JsonFiles.ReadAsync<RetentionJournal>(journalPath);
            if (journal.Plan != plan && Hash(journal.Plan) != Hash(plan)) throw new InvalidDataException("Cleanup journal changed.");
            if(journal.Items is null && journal.Phase!="Completed")throw new InvalidDataException("Legacy incomplete cleanup requires review; no automatic adoption.");
            if (journal.Phase == "Completed")
            {
                if(plan.Candidates.Any(c=>Directory.Exists(SafePath.ResolveInsideChecked(settings.Root,c.Path))))
                    throw new InvalidDataException("A completed plan cannot delete recreated content; make a new plan.");
                return new { Completed = true, Resumed = true };
            }
            if (References(settings.Root).Hash != plan.ReferenceHash) throw new InvalidDataException("References changed; cleanup remains paused.");
        }
        else
        {
            var actual = await PlanAsync(settings, plan.Jobs, plan.Temporary);
            if (Hash(actual with {PlanId=plan.PlanId,Fingerprint=""}) != plan.Fingerprint) throw new InvalidDataException("Cleanup plan changed; inspect and confirm a new plan.");
            journal = new(plan,0,"Pending",[]); await JsonFiles.WriteAsync(journalPath,journal);
        }
        for (var index=journal.NextIndex;index<plan.Candidates.Count;index++)
        {
            var candidate=plan.Candidates[index];
            var source=SafePath.ResolveInsideChecked(settings.Root,candidate.Path);
            var privateRoot=SafePath.ResolveInsideChecked(settings.Root,".retention-quarantine");MaintenanceStorage.PrivateDirectory(privateRoot);
            var privatePlan=SafePath.ResolveInsideChecked(privateRoot,plan.Fingerprint);MaintenanceStorage.PrivateDirectory(privatePlan);
            var quarantine=SafePath.ResolveInsideChecked(privatePlan,index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var item=journal.Items!.SingleOrDefault(value=>value.Index==index);
            if(item is null)
            {
                if(!Directory.Exists(source))throw new InvalidDataException("Confirmed source disappeared before movement.");
                item=new(index,"MoveIntent",MaintenanceStorage.DirectoryIdentity(source),Path.GetRelativePath(settings.Root,quarantine));journal.Items!.Add(item);
                await JsonFiles.WriteAsync(journalPath,journal);Boundary.Value?.Invoke("move-intent");
            }
            async Task Transition(string phase)
            {
                item=item! with{Phase=phase};journal.Items![index]=item;await JsonFiles.WriteAsync(journalPath,journal);
            }
            if(item.Phase=="MoveIntent")
            {
                if(Directory.Exists(quarantine))
                {if(MaintenanceStorage.DirectoryIdentity(quarantine)!=item.DirectoryIdentity)throw new InvalidDataException("Quarantine identity changed.");}
                else
                {
                    if(!Directory.Exists(source) || MaintenanceStorage.DirectoryIdentity(source)!=item.DirectoryIdentity)throw new InvalidDataException("Source was recreated; make a new plan.");
                    Directory.Move(source,quarantine);
                }
                Boundary.Value?.Invoke("quarantine");await Transition("Quarantined");
            }
            if(item.Phase=="Quarantined")
            {
                var actual=new List<BackupFile>();foreach(var file in DistributionBackup.Files(quarantine))actual.Add(new(Path.GetRelativePath(quarantine,file).Replace('\\','/'),new FileInfo(file).Length,await Hashing.Sha256FileAsync(file)));
                if(Hash(actual)!=Hash(candidate.Files))throw new InvalidDataException("Quarantined content changed.");
                if(Hash(Directories(quarantine))!=Hash(candidate.Directories!))throw new InvalidDataException("Quarantined directories changed.");
                await Transition("DeleteIntent");Boundary.Value?.Invoke("delete-intent");
            }
            if(item.Phase=="DeleteIntent")
            {
                if(Directory.Exists(quarantine))
                {
                    if(MaintenanceStorage.DirectoryIdentity(quarantine)!=item.DirectoryIdentity)throw new InvalidDataException("Quarantine identity changed.");
                    var remaining=DistributionBackup.Files(quarantine);var expected=candidate.Files.ToDictionary(file=>file.Path,StringComparer.Ordinal);
                    foreach(var path in remaining)
                    {
                        var relative=Path.GetRelativePath(quarantine,path).Replace('\\','/');
                        if(!expected.TryGetValue(relative,out var file) || new FileInfo(path).Length!=file.Bytes)throw new InvalidDataException("Unexpected remaining cleanup file.");
                        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
                        {
                            var hash=Convert.ToHexString(await SHA256.HashDataAsync(held)).ToLowerInvariant();
                            if(hash!=file.Sha256)throw new InvalidDataException("Remaining cleanup file changed.");
                            File.Delete(path);
                        }
                        Boundary.Value?.Invoke("file-delete");
                    }
                    DeleteApprovedEmptyDirectories(quarantine,candidate.Directories!);
                }
                Boundary.Value?.Invoke("delete");await Transition("Deleted");
            }
            if(item.Phase!="Deleted")throw new InvalidDataException("Unknown cleanup phase.");
            authority.CommitOffline(maintenance, snapshot =>
            {
                var jobs = IntakeJobs.Read(snapshot);
                var id = $"{plan.PlanId}:{index}";
                var existing = jobs.MaintenanceDeletions.Find(item => item.Id == id);
                var treeHash = Hash(candidate.Files);
                if (existing is not null)
                {
                    if (existing.Path != candidate.Path || existing.Job != candidate.JobId || existing.TreeHash != treeHash)
                        throw new InvalidDataException("Retention ledger conflicts with the confirmed deletion.");
                    return CommitPlan.Audit();
                }

                var now = DateTimeOffset.UtcNow;
                jobs.MaintenanceDeletions.Add(new(id, candidate.Path, candidate.JobId, treeHash, now));
                return jobs.Plan(new AuthorityEvent(now, "retention-deleted", id));
            });
            journal=journal with {NextIndex=index+1,Phase="Deleting"};await JsonFiles.WriteAsync(journalPath,journal);
        }
        authority.CommitOffline(maintenance, snapshot =>
            authority.HasAudit(snapshot, "retention-applied", plan.Fingerprint) ? CommitPlan.Audit()
                : CommitPlan.Audit(new AuthorityEvent(DateTimeOffset.UtcNow, "retention-applied", plan.Fingerprint)));
        await JsonFiles.WriteAsync(journalPath,journal with {Phase="Completed"});
        return new {Completed=true,DeletedDirectories=plan.Candidates.Count,Bytes=plan.Candidates.Sum(c=>c.Bytes),PublicReleasesDeleted=0};
    }
}
