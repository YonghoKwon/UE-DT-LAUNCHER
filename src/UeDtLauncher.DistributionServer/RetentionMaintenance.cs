using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record RetentionDirectory(string Path,string Identity);
public sealed record RetentionCandidate(string Path, string? JobId, long Bytes, IReadOnlyList<BackupFile> Files,IReadOnlyList<RetentionDirectory>? Directories=null);
public sealed record RetentionPlan(int SchemaVersion, string Root, IReadOnlyList<string> Jobs, IReadOnlyList<string> Temporary,
    string ReferenceHash, IReadOnlyList<RetentionCandidate> Candidates, string Fingerprint, string PlanId);
public sealed record RetentionJournal(RetentionPlan Plan, int NextIndex, string Phase,List<RetentionItem>? Items=null);
public sealed record RetentionItem(int Index,string Phase,string DirectoryIdentity,string Quarantine);

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
    private static (List<(string Id, string Source, string State, string? Snapshot)> Jobs, HashSet<string> Referenced, string Hash) References(string root)
    {
        using var db = DistributionBackup.Open(root, true); using var tx = db.BeginTransaction(deferred: true); using var q = db.CreateCommand(); q.Transaction = tx;
        var references = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var jobs = new List<(string, string, string, string?)>(); var proof = new List<string>();
        q.CommandText = "SELECT id,source,state,snapshot FROM jobs ORDER BY id";
        using (var r = q.ExecuteReader()) while (r.Read())
        {
            var job = (r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)); jobs.Add(job);
            proof.Add(Hash(new{Id=job.Item1,Source=job.Item2,State=job.Item3,Snapshot=job.Item4}));
            if (job.Item3 is not ("failed" or "rejected")) { references.Add(job.Item2); if (job.Item4 is not null) references.Add(job.Item4); }
        }
        foreach (var sql in new[] { "SELECT id,directory FROM releases ORDER BY id", "SELECT CAST(id AS TEXT),release_id FROM promotions ORDER BY id", "SELECT job,COALESCE(scratch,''),COALESCE(snapshot,'') FROM active_work ORDER BY job" })
        {
            q.CommandText = sql; using var r = q.ExecuteReader();
            while (r.Read())
            {
                var values=Enumerable.Range(1,r.FieldCount-1).Select(r.GetString).ToArray();
                proof.Add(Hash(new{Id=r.GetString(0),Values=values}));
                foreach(var path in values)if(Path.IsPathRooted(path))references.Add(Path.GetFullPath(path));
            }
        }
        tx.Commit(); return (jobs, references, Hash(proof));
    }
    public static object Inspect(DistributionSettings settings)
    {
        var root = Path.GetFullPath(settings.Root); var current = References(root);
        return new { Jobs = current.Jobs.Select(j => new { j.Id, j.State, Protection = j.State is "failed" or "rejected" ? "explicit selection required" : "published/pending/active history protected" }),
            PublicDeletionAllowed = false, PromotionHistoryProtected = true, ReferenceHash = current.Hash };
    }
    public static async Task<RetentionPlan> PlanAsync(DistributionSettings settings, IReadOnlyList<string> selectedJobs, IReadOnlyList<string> temporary)
    {
        var root = Path.GetFullPath(settings.Root); var current = References(root);
        var candidates = new List<RetentionCandidate>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        async Task Add(string path, string? jobId)
        {
            path = Path.GetFullPath(path); var relative = Path.GetRelativePath(root, path);
            _ = SafePath.ResolveInsideChecked(root, relative);
            if (!(relative.StartsWith("incoming" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("processing" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("archive" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidDataException("Only nonpublic failed/rejected uploads or unreferenced processing children are eligible.");
            if (!paths.Add(path) || current.Referenced.Any(r => string.Equals(path,r,Comparison) || r.StartsWith(path + Path.DirectorySeparatorChar, Comparison) || path.StartsWith(r + Path.DirectorySeparatorChar, Comparison)))
                throw new InvalidDataException("Cleanup target is referenced or duplicated.");
            if (current.Jobs.Any(j => j.Id != jobId && (j.Source == path || j.Snapshot == path))) throw new InvalidDataException("Another job references this target.");
            if (!Directory.Exists(path)) return;
            var files = new List<BackupFile>();
            foreach (var file in DistributionBackup.Files(path)) files.Add(new(Path.GetRelativePath(path, file).Replace('\\','/'), new FileInfo(file).Length, await Hashing.Sha256FileAsync(file)));
            candidates.Add(new(relative.Replace('\\','/'), jobId, files.Sum(f => f.Bytes), files,Directories(path)));
        }
        foreach (var id in selectedJobs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var job = current.Jobs.SingleOrDefault(j => j.Id == id);
            if (job.Id is null || job.State is not ("failed" or "rejected")) throw new InvalidDataException("Selected job is missing or protected.");
            if (job.Snapshot is not null) await Add(job.Snapshot, id);
            await Add(job.Source, id);
        }
        foreach (var path in temporary.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (Path.GetDirectoryName(path.Replace('/',Path.DirectorySeparatorChar)) != "processing") throw new InvalidDataException("Select immediate processing child directories only.");
            await Add(SafePath.ResolveInsideChecked(root,path), null);
        }
        var plan = new RetentionPlan(2, root, selectedJobs.Order(StringComparer.Ordinal).ToArray(), temporary.Order(StringComparer.Ordinal).ToArray(), current.Hash, candidates, "", Guid.NewGuid().ToString("N"));
        return plan with { Fingerprint = Hash(plan) };
    }
    public static async Task<object> ApplyAsync(DistributionSettings settings, RetentionPlan plan, bool confirm)
    {
        if(plan.SchemaVersion!=2 || plan.Candidates.Any(c=>c.Directories is null))
            throw new InvalidDataException("Cleanup requires a new schema 2 plan with approved directory identities; preserve old journals for review.");
        if (!confirm || !Guid.TryParseExact(plan.PlanId,"N",out _) || Path.GetFullPath(settings.Root) != plan.Root || Hash(plan with { Fingerprint = "" }) != plan.Fingerprint)
            throw new InvalidDataException("Valid plan and explicit --confirm are required.");
        using var maintenance = DistributionMaintenanceLease.Acquire(settings.Root, true, create:false);
        using var oldServer = new AuthenticationProcessLease(settings.Root);
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
            using(var db=DistributionBackup.Open(settings.Root,false))
            using(var tx=db.BeginTransaction())
            {
                using var q=db.CreateCommand();q.Transaction=tx;q.CommandText="INSERT OR IGNORE INTO maintenance_deletions(id,path,job,tree_hash,at) VALUES($id,$path,$job,$hash,$at)";
                q.Parameters.AddWithValue("$id",plan.PlanId+":"+index);q.Parameters.AddWithValue("$path",candidate.Path);q.Parameters.AddWithValue("$job",(object?)candidate.JobId??DBNull.Value);q.Parameters.AddWithValue("$hash",Hash(candidate.Files));q.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));q.ExecuteNonQuery();tx.Commit();
            }
            journal=journal with {NextIndex=index+1,Phase="Deleting"};await JsonFiles.WriteAsync(journalPath,journal);
        }
        using(var db=DistributionBackup.Open(settings.Root,false))
        using(var tx=db.BeginTransaction())
        {
            using var q=db.CreateCommand();q.Transaction=tx;
            q.CommandText="SELECT COUNT(*) FROM audit WHERE action='retention-applied' AND job=$id";q.Parameters.AddWithValue("$id",plan.Fingerprint);
            if(Convert.ToInt64(q.ExecuteScalar())==0)CredentialLifecycle.Audit(db,tx,"retention-applied",plan.Fingerprint);
            tx.Commit();
        }
        await JsonFiles.WriteAsync(journalPath,journal with {Phase="Completed"});
        return new {Completed=true,DeletedDirectories=plan.Candidates.Count,Bytes=plan.Candidates.Sum(c=>c.Bytes),PublicReleasesDeleted=0};
    }
}
