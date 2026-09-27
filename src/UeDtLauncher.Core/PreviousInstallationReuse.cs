using System.Diagnostics;
using System.Text.Json;

namespace UeDtLauncher;

/// <summary>Local records are hints only: authenticated target hashes remain the trust boundary.</summary>
internal sealed class PreviousInstallationReuse
{
    private const int MaxDiscoveryRecords = 256;
    private const int MaxCandidates = 3;
    private readonly IReadOnlyList<Candidate> _candidates;
    private readonly string _installRoot;

    private PreviousInstallationReuse(string installRoot, IReadOnlyList<Candidate> candidates)
    {
        _installRoot = installRoot; _candidates = candidates;
    }

    internal static async Task<PreviousInstallationReuse?> DiscoverAsync(LauncherConfig config,
        ManifestDocument target, bool targetExisted, CancellationToken token)
    {
        var selected = config.SelectedRelease;
        if (targetExisted || config.RepairMode || !config.Performance.ReusePreviousInstallations ||
            selected is null || config.VersionedInstallRoot is null ||
            !config.CatalogAuthenticated || !target.SignatureVerified) return null;
        selected.Validate();
        if (!Same(selected.ProjectId, target.Manifest.AppId) || !Same(selected.Version, target.Manifest.Version) ||
            !Same(selected.Platform, target.Manifest.Platform) || !Same(selected.Channel, target.Manifest.Channel)) return null;

        var installRoot = Path.GetFullPath(config.VersionedInstallRoot);
        var stateRoot = Path.GetFullPath(config.StateRootDir);
        var candidates = new List<Candidate>();
        try
        {
            // Only the selected project/environment/channel directory is inspected. No recursive scan.
            var track = SafePath.ResolveInsideChecked(stateRoot, string.Join('/', selected.ProjectId, selected.Environment, selected.Channel));
            if (!Directory.Exists(track)) return null;
            var versions = Directory.EnumerateDirectories(track).Take(MaxDiscoveryRecords + 1).ToArray();
            // Bound work even if a local record tree has been filled with arbitrary directories.
            if (versions.Length > MaxDiscoveryRecords) return null;
            var records = new List<(ReleaseSelection Selection, DateTimeOffset InstalledAt)>();
            foreach (var versionDirectory in versions)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var version = Path.GetFileName(versionDirectory);
                    if (Same(version, selected.Version)) continue;
                    var selection = selected with { Version = version };
                    selection.Validate();
                    var statePath = SafePath.ResolveInsideChecked(stateRoot, selection.ReleaseId + "/install-state.json");
                    if (!File.Exists(statePath)) continue;
                    var state = await ReadBoundedAsync<InstallState>(statePath, 64 * 1024, token);
                    if (!Same(state.Version, version) || !Same(state.Environment, selected.Environment) ||
                        !Same(state.Channel, selected.Channel) || !Same(state.Platform, selected.Platform) ||
                        string.IsNullOrWhiteSpace(state.ManifestSha256) ||
                        !DateTimeOffset.TryParse(state.InstalledAtUtc, out var installedAt)) continue;
                    var journal = SafePath.ResolveInsideChecked(stateRoot, selection.ReleaseId + "/transaction.json");
                    if (File.Exists(journal))
                    {
                        var transaction = await ReadBoundedAsync<UpdateTransactionJournal>(journal, 1024 * 1024, token);
                        if (transaction.Status != UpdateTransactionStatus.Committed) continue;
                    }
                    var source = SafePath.ResolveInsideChecked(installRoot, selection.ReleaseId);
                    if (!Directory.Exists(source)) continue;
                    records.Add((selection, installedAt));
                }
                catch (Exception ex) when (IsUnavailable(ex)) { /* Local hints are optional. */ }
            }
            foreach (var record in records.OrderByDescending(r => r.InstalledAt)
                         .ThenBy(r => r.Selection.Version, StringComparer.Ordinal).Take(MaxCandidates))
            {
                try
                {
                    var source = SafePath.ResolveInsideChecked(installRoot, record.Selection.ReleaseId);
                    var manifestPath = SafePath.ResolveInsideChecked(stateRoot, record.Selection.ReleaseId + "/installed-manifest.json");
                    var manifest = await ReadBoundedAsync<LauncherManifest>(manifestPath, config.Security.MaxManifestBytes, token);
                    if (manifest.Files is null || manifest.Files.Any(file => file is null)) continue;
                    LauncherEngine.ValidateManifest(manifest);
                    if (!Same(manifest.AppId, selected.ProjectId) || !Same(manifest.Platform, selected.Platform) ||
                        !Same(manifest.Channel, selected.Channel) || !Same(manifest.Version, record.Selection.Version) ||
                        manifest.Files.Count > config.Security.MaxManifestFiles) continue;
                    candidates.Add(new Candidate(source, manifest.Files.ToDictionary(file => Canonical(file.Path), SafePath.FileSystemComparer)));
                }
                catch (Exception ex) when (IsUnavailable(ex)) { }
            }
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return null; }
        return candidates.Count == 0 ? null : new PreviousInstallationReuse(installRoot, candidates);
    }

    internal async Task<bool> TryCopyAsync(ManifestFile target, string stagingPath,
        Func<string, string, CancellationToken, Task<bool>> verify,
        ClientPerformanceCounters metrics, CancellationToken token)
    {
        foreach (var candidate in _candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!candidate.Files.TryGetValue(Canonical(target.Path), out var sourceFile) ||
                sourceFile.Size != target.Size || !Same(sourceFile.Sha256, target.Sha256)) continue;
            try
            {
                // Check from the configured root, not merely the version directory, so a linked
                // project/version/platform ancestor cannot redirect a reuse source.
                var relative = Path.GetRelativePath(_installRoot, Path.Combine(candidate.InstallDirectory, sourceFile.Path));
                var source = SafePath.ResolveInsideChecked(_installRoot, relative);
                if (!File.Exists(source) || new FileInfo(source).Length != target.Size) continue;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        copied += read;
                        if (copied > target.Size) throw new IOException("Reuse source changed size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (copied != target.Size) throw new IOException("Reuse source was truncated.");
                    await output.FlushAsync(token);
                }
                finally { metrics.AddCopy(started); }
                if (await verify(stagingPath, target.Sha256, token))
                {
                    metrics.AddReusedBytes(target.Size);
                    return true;
                }
            }
            catch (Exception ex) when (IsUnavailable(ex)) { /* Missing/locked/corrupt sources fall back to HTTP. */ }
            // This is our own newly-created staging destination, never the previous installation.
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
        return false;
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string path) => path.Replace('\\', '/');
    private static bool IsUnavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or InvalidOperationException or ArgumentException;

    private static async Task<T> ReadBoundedAsync<T>(string path, int maximum, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Local installation metadata exceeds limit.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Local installation metadata exceeds limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), JsonFiles.Options)
               ?? throw new InvalidDataException("Local installation metadata is empty.");
    }

    private sealed record Candidate(string InstallDirectory, IReadOnlyDictionary<string, ManifestFile> Files);
}
