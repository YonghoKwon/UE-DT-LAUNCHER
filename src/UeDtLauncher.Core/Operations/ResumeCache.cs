using System.Security.Cryptography;
using System.Text;

namespace UeDtLauncher;

public sealed record ResumeRecord(int SchemaVersion, string ReleaseId, string ManifestSha256,
    string Owner, string Session, long BudgetBytes);

/// <summary>Installation-private, opt-in cache. Never a shared content cache or a trust source.</summary>
internal sealed class ResumeCache
{
    private readonly string root;
    private ResumeCache(string root) => this.root = root;
    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static ResumeCache? Open(LauncherConfig config, ManifestDocument document)
    {
        var budget = config.Performance.ResumeCacheBytes;
        if (budget is null or <= 0 || !document.SignatureVerified || config.SelectedRelease is null) return null;
        var digest = Digest(document.Json);
        if (document.Manifest.Files.Sum(file => checked(file.Size)) > budget) return null;
        var parent = Path.Combine(Path.GetDirectoryName(config.StagingDir)!, "resume-cache");
        SafePath.EnsureNoReparsePoints(Path.GetFullPath(config.StateRootDir), parent);
        // Do not evict another authenticated manifest implicitly. The budget is for this installation.
        if (Directory.Exists(parent) && Directory.EnumerateDirectories(parent).Any(path => Path.GetFileName(path) != digest)) return null;
        var root = SafePath.ResolveInsideChecked(parent, digest);
        if (Directory.Exists(root))
        {
            long stored = 0;
            var entries = Directory.EnumerateFiles(root).Take(document.Manifest.Files.Count * 2 + 2).ToArray();
            if (entries.Length > document.Manifest.Files.Count * 2 + 1) return null;
            foreach (var entry in entries)
            {
                SafePath.EnsureNoReparsePoints(Path.GetFullPath(config.StateRootDir), entry);
                if (Path.GetFileName(entry) != "resume.json") stored = checked(stored + new FileInfo(entry).Length);
            }
            if (stored > budget) return null;
        }
        var owner = RuntimeIdentities.Current();
        var expected = new ResumeRecord(1, config.SelectedRelease.ReleaseId, digest, owner.Owner, owner.Session, budget.Value);
        var record = SafePath.ResolveInsideChecked(root, "resume.json");
        if (File.Exists(record))
        {
            if (new FileInfo(record).Length > 64 * 1024 || JsonFiles.ReadAsync<ResumeRecord>(record).GetAwaiter().GetResult() != expected)
                throw new InvalidDataException("Resume cache ownership or authenticated manifest changed.");
        }
        else JsonFiles.WriteAsync(record, expected).GetAwaiter().GetResult();
        return new(root);
    }
    internal string Partial(ManifestFile file) => SafePath.ResolveInsideChecked(root, Digest(file.Path) + ".partial");
    private string Complete(ManifestFile file) => SafePath.ResolveInsideChecked(root, Digest(file.Path) + ".verified");
    internal async Task<bool> TryCopyAsync(ManifestFile file, string staging, CancellationToken token)
    {
        var source = Complete(file);
        if (!File.Exists(source)) return false;
        if (new FileInfo(source).Length != file.Size || !await Hashing.Sha256MatchesAsync(source, file.Sha256, token))
        { File.Delete(source); return false; }
        File.Copy(source, staging, overwrite: false);
        if (await Hashing.Sha256MatchesAsync(staging, file.Sha256, token)) return true;
        File.Delete(staging); return false;
    }
    internal async Task StoreAsync(ManifestFile file, string staging, CancellationToken token)
    {
        var target = Complete(file);
        var temporary = target + ".new";
        File.Copy(staging, temporary, overwrite: true);
        if (!await Hashing.Sha256MatchesAsync(temporary, file.Sha256, token)) throw new InvalidDataException("Resume cache copy changed.");
        File.Move(temporary, target, overwrite: true);
    }
}
