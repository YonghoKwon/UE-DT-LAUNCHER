namespace UeDtLauncher;

public sealed class CatalogReleaseUpdate
{
    public string ProjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Environment { get; set; } = "prod";
    public string Channel { get; set; } = "stable";
    public string Platform { get; set; } = "windows-x64";
    public string ManifestUrl { get; set; } = string.Empty;
    public string? ManifestSignatureUrl { get; set; }
    public List<string> AllowedClientProfiles { get; set; } = new();
    public string? Notes { get; set; }
    public bool SetLatest { get; set; }
}

/// <summary>
/// Cross-platform replacement for tools/update-catalog.ps1: upserts or removes releases in a
/// distribution catalog. Releases are keyed by version + environment + channel + platform.
/// </summary>
public static class CatalogUpdater
{
    public static async Task<DistributionCatalog> UpsertReleaseAsync(string catalogPath, CatalogReleaseUpdate update, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.ProjectId)) throw new ArgumentException("projectId is required.");
        if (string.IsNullOrWhiteSpace(update.Version)) throw new ArgumentException("version is required.");
        if (string.IsNullOrWhiteSpace(update.ManifestUrl)) throw new ArgumentException("manifestUrl is required.");
        if (update.AllowedClientProfiles.Count == 0) throw new ArgumentException("At least one allowed client profile is required.");

        var catalog = await LoadOrCreateAsync(catalogPath, cancellationToken);
        var project = FindProject(catalog, update.ProjectId);
        if (project is null)
        {
            project = new DistributionProject { ProjectId = update.ProjectId };
            catalog.Projects.Add(project);
        }

        if (!string.IsNullOrWhiteSpace(update.DisplayName)) project.DisplayName = update.DisplayName;
        else if (string.IsNullOrWhiteSpace(project.DisplayName)) project.DisplayName = update.ProjectId;

        if (update.SetLatest)
        {
            foreach (var sibling in project.Releases.Where(release => SameTrack(release, update.Environment, update.Channel, update.Platform)))
            {
                sibling.IsLatest = false;
            }
        }

        var newRelease = new DistributionRelease
        {
            Version = update.Version,
            Channel = update.Channel,
            Environment = update.Environment,
            Platform = update.Platform,
            ManifestUrl = update.ManifestUrl,
            ManifestSignatureUrl = update.ManifestSignatureUrl,
            AllowedClientProfiles = update.AllowedClientProfiles.ToList(),
            IsLatest = update.SetLatest,
            Notes = update.Notes
        };

        var index = FindReleaseIndex(project, update.Version, update.Environment, update.Channel, update.Platform);
        if (index >= 0) project.Releases[index] = newRelease;
        else project.Releases.Add(newRelease);

        catalog.GeneratedAt = DateTimeOffset.UtcNow.ToString("O");
        await JsonFiles.WriteAsync(catalogPath, catalog, cancellationToken);
        return catalog;
    }

    public static async Task<DistributionCatalog> RemoveReleaseAsync(string catalogPath, string projectId, string version, string environment, string channel, string platform, bool removeProjectIfEmpty = false, CancellationToken cancellationToken = default)
    {
        var catalog = await LoadOrCreateAsync(catalogPath, cancellationToken);
        var project = FindProject(catalog, projectId);
        if (project is not null)
        {
            var index = FindReleaseIndex(project, version, environment, channel, platform);
            if (index >= 0) project.Releases.RemoveAt(index);
            if (removeProjectIfEmpty && project.Releases.Count == 0) catalog.Projects.Remove(project);
        }

        catalog.GeneratedAt = DateTimeOffset.UtcNow.ToString("O");
        await JsonFiles.WriteAsync(catalogPath, catalog, cancellationToken);
        return catalog;
    }

    private static async Task<DistributionCatalog> LoadOrCreateAsync(string catalogPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(catalogPath)) return new DistributionCatalog();
        return await JsonFiles.ReadAsync<DistributionCatalog>(catalogPath, cancellationToken);
    }

    private static DistributionProject? FindProject(DistributionCatalog catalog, string projectId) =>
        catalog.Projects.FirstOrDefault(project => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));

    private static int FindReleaseIndex(DistributionProject project, string version, string environment, string channel, string platform) =>
        project.Releases.FindIndex(release =>
            string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase) &&
            SameTrack(release, environment, channel, platform));

    private static bool SameTrack(DistributionRelease release, string environment, string channel, string platform) =>
        string.Equals(release.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(release.Channel, channel, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(release.Platform, platform, StringComparison.OrdinalIgnoreCase);
}
