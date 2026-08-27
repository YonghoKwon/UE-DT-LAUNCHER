namespace UeDtLauncher.Gui;

public sealed class CatalogSnapshot
{
    public string Status { get; init; } = "카탈로그 미확인";
    public List<CatalogProjectOption> Projects { get; init; } = new();
    public List<CatalogReleaseOption> Releases { get; init; } = new();
}

public sealed class CatalogProjectOption
{
    public string ProjectId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int ReleaseCount { get; init; }
}

public sealed class CatalogReleaseOption
{
    public string ProjectId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public bool IsLatest { get; init; }
    public string? Notes { get; init; }
}

public static class CatalogSnapshotService
{
    public static async Task<CatalogSnapshot> LoadAsync(LauncherConfig config, string currentPlatform, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.CatalogUrl))
        {
            return new CatalogSnapshot { Status = "직접 manifest 모드" };
        }

        using var httpClient = SecureHttpClientFactory.Create(config);
        var catalog = await CatalogResolver.DownloadCatalogAsync(config, httpClient, cancellationToken: cancellationToken);

        var allowedProjects = new List<CatalogProjectOption>();
        var allowedReleases = new List<CatalogReleaseOption>();

        foreach (var project in catalog.Projects)
        {
            var releases = project.Releases
                .Where(release => string.Equals(release.Platform, currentPlatform, StringComparison.OrdinalIgnoreCase))
                .Where(release => release.AllowedClientProfiles.Any(profile => string.Equals(profile, config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (releases.Count == 0) continue;

            allowedProjects.Add(new CatalogProjectOption
            {
                ProjectId = project.ProjectId,
                DisplayName = string.IsNullOrWhiteSpace(project.DisplayName) ? project.ProjectId : project.DisplayName,
                ReleaseCount = releases.Count
            });

            allowedReleases.AddRange(releases.Select(release => new CatalogReleaseOption
            {
                ProjectId = project.ProjectId,
                Version = release.Version,
                Environment = release.Environment,
                Channel = release.Channel,
                Platform = release.Platform,
                IsLatest = release.IsLatest,
                Notes = release.Notes
            }));
        }

        return new CatalogSnapshot
        {
            Status = $"카탈로그 확인 완료 · 프로젝트 {allowedProjects.Count}개 · 릴리스 {allowedReleases.Count}개",
            Projects = allowedProjects
                .OrderBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Releases = allowedReleases
                .OrderByDescending(release => release.IsLatest)
                .ThenByDescending(release => VersionSortKey.Parse(release.Version))
                .ToList()
        };
    }

    private readonly record struct VersionSortKey(int Major, int Minor, int Patch, string Raw) : IComparable<VersionSortKey>
    {
        public static VersionSortKey Parse(string raw)
        {
            var cleaned = raw.Trim().TrimStart('v', 'V');
            var main = cleaned.Split('-', '+')[0];
            var parts = main.Split('.');
            return new VersionSortKey(ParsePart(parts, 0), ParsePart(parts, 1), ParsePart(parts, 2), raw);
        }

        public int CompareTo(VersionSortKey other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0) return minor;
            var patch = Patch.CompareTo(other.Patch);
            if (patch != 0) return patch;
            return string.Compare(Raw, other.Raw, StringComparison.OrdinalIgnoreCase);
        }

        private static int ParsePart(string[] parts, int index)
        {
            if (index >= parts.Length) return 0;
            return int.TryParse(parts[index], out var value) ? value : 0;
        }
    }
}
