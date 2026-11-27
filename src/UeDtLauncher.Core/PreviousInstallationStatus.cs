using System.Text.Json;

namespace UeDtLauncher;

// Display hints only. Neither hint grants execution or certifies payload health.
internal static class PreviousInstallationStatus
{
    private sealed record Candidate(PreviousInstallation Installation, DateTimeOffset InstalledAt, bool GeneralAllowed);
    private sealed record InstallEvidence(string? Version, string? Environment, string? Channel, string? Platform,
        string? ManifestSha256, string? InstalledAtUtc);

    internal static async Task<(PreviousInstallation? Previous, RecommendationHistory? History)> InspectAsync(
        LauncherConfig config, bool selectedInstalled, CancellationToken token = default)
    {
        var selected = config.SelectedRelease;
        if (!config.CatalogAuthenticated || config.AuthenticatedCatalog is null ||
            selected is null || config.VersionedInstallRoot is null)
            return (null, null);

        try
        {
            const int limit = 256;
            var releases = config.AuthenticatedCatalog.Projects.Where(project => project.ProjectId == selected.ProjectId)
                .SelectMany(project => project.Releases)
                .Where(release => release.Environment == selected.Environment && release.Channel == selected.Channel &&
                    release.Platform == selected.Platform)
                .Take(limit + 1).ToArray();
            if (releases.Length > limit || !releases.Any(release => release.Version == selected.Version))
                return (null, new(null));

            var track = SafePath.ResolveInsideChecked(config.StateRootDir,
                string.Join('/', selected.ProjectId, selected.Environment, selected.Channel));
            if (!Directory.Exists(track) || Directory.EnumerateDirectories(track).Take(limit + 1).Count() > limit)
                return (null, new(null));

            var candidates = new List<Candidate>();
            foreach (var release in releases)
            {
                token.ThrowIfCancellationRequested();
                var selection = selected with { Version = release.Version };
                if (selection == selected && !selectedInstalled) continue;
                try
                {
                    selection.Validate();
                    var candidate = await ReadCandidateAsync(config, selection, release, token);
                    if (candidate is not null) candidates.Add(candidate);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                    JsonException or InvalidOperationException or ArgumentException)
                {
                    // A damaged installation cannot invalidate evidence for other releases.
                }
            }

            var previous = selectedInstalled ? null : candidates.OrderByDescending(candidate => candidate.InstalledAt)
                .ThenBy(candidate => candidate.Installation.Release.Version, StringComparer.Ordinal)
                .Select(candidate => candidate.Installation).FirstOrDefault();

            // Include the selected release in the ordering so an older use of a higher version
            // cannot trigger another notice after the recommended release has been activated.
            // A running record takes priority so the GUI still rechecks it before changing versions.
            var current = candidates.Where(candidate => candidate.GeneralAllowed)
                .Select(candidate => ReadActivation(config, candidate))
                .OrderByDescending(activation => activation.Running)
                .ThenBy(activation => activation.At.HasValue)
                .ThenByDescending(activation => activation.At)
                .ThenByDescending(activation => activation.Release == selected)
                .ThenBy(activation => activation.Release.Version, StringComparer.Ordinal)
                .FirstOrDefault();
            return (previous, new(current.At.HasValue ? current.Release : null));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or ArgumentException)
        {
            return (null, new(null));
        }
    }

    private static async Task<Candidate?> ReadCandidateAsync(LauncherConfig config, ReleaseSelection selection,
        DistributionRelease release, CancellationToken token)
    {
        var statePath = SafePath.ResolveInsideChecked(config.StateRootDir, selection.ReleaseId + "/install-state.json");
        if (!File.Exists(statePath)) return null;
        var state = await ReadAsync<InstallEvidence>(statePath, 64 * 1024, token);
        if (state.Version != selection.Version || state.Environment != selection.Environment ||
            state.Channel != selection.Channel || state.Platform != selection.Platform ||
            string.IsNullOrWhiteSpace(state.ManifestSha256) || state.InstalledAtUtc is not { } installedAt ||
            !DateTimeOffset.TryParse(installedAt, out var date))
            return null;

        var journal = SafePath.ResolveInsideChecked(config.StateRootDir, selection.ReleaseId + "/transaction.json");
        if (File.Exists(journal) &&
            (await ReadAsync<UpdateTransactionJournal>(journal, 1024 * 1024, token)).Status != UpdateTransactionStatus.Committed)
            return null;

        var manifestPath = SafePath.ResolveInsideChecked(config.StateRootDir, selection.ReleaseId + "/installed-manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = await ReadAsync<LauncherManifest>(manifestPath, config.Security.MaxManifestBytes, token);
        LauncherEngine.ValidateManifest(manifest);
        if (manifest.AppId != selection.ProjectId || manifest.Version != selection.Version ||
            manifest.Platform != selection.Platform || manifest.Channel != selection.Channel)
            return null;

        var app = SafePath.ResolveInsideChecked(config.VersionedInstallRoot!, selection.ReleaseId);
        if (!File.Exists(SafePath.ResolveInsideChecked(app, manifest.EntryPoint))) return null;
        return new(new(selection, installedAt), date,
            release.AllowedClientProfiles.Contains("general", StringComparer.OrdinalIgnoreCase));
    }

    private static (ReleaseSelection Release, DateTimeOffset? At, bool Running) ReadActivation(
        LauncherConfig config, Candidate candidate)
    {
        var selection = candidate.Installation.Release;
        try
        {
            var bound = new LauncherConfig { StateRootDir = config.StateRootDir, VersionedInstallRoot = config.VersionedInstallRoot };
            VersionedReleasePaths.Bind(bound, selection);
            var path = SafePath.ResolveInsideChecked(config.StateRootDir, selection.ReleaseId + "/runtime-state.json");
            var written = File.GetLastWriteTimeUtc(path);
            var runtime = RuntimeStore.Read(bound);
            if (runtime is { State: RuntimeState.Quiescent, Origin: "launch-aborted" })
                // The aborted attempt may have replaced the last successful run. Its recency is unknown.
                return (selection, null, false);
            // Use the write time only alongside a validated start/completion record from the same snapshot.
            if (runtime is { State: RuntimeState.Running } or
                { State: RuntimeState.Quiescent, Origin: "supervisor-completed" } &&
                File.GetLastWriteTimeUtc(path) == written)
                return (selection, written > candidate.InstalledAt ? written : candidate.InstalledAt,
                    runtime.State == RuntimeState.Running);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or RuntimeDataException)
        {
            // The install record still supports a hint. Runtime inspection independently gates actions.
        }
        return (selection, candidate.InstalledAt, false);
    }

    private static async Task<T> ReadAsync<T>(string path, long limit, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > limit) throw new InvalidDataException("Installation metadata is too large.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Installation metadata grew.");
            output.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), JsonFiles.Options)
            ?? throw new InvalidDataException("Empty installation metadata.");
    }
}
