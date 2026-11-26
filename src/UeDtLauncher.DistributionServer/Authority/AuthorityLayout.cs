namespace UeDtLauncher.Distribution.Authority;

internal enum AuthorityRootKind { LegacyDatabase, ExistingDistribution, UnrecognizedLayout, Damaged, UnsupportedFileSystem, MissingRoot }

internal sealed record AuthorityLayout(
    string AuthorityDirectory = "authority",
    string CatalogDirectory = "catalog",
    string JobsDirectory = "jobs",
    string AuthDirectory = "auth",
    string SequenceDirectory = "sequence",
    string EventsDirectory = "events",
    string ReleaseMetadataDirectory = "release-meta",
    string ProgressDirectory = "progress",
    IReadOnlyList<string>? ExistingDistributionMarkers = null)
{
    internal static AuthorityLayout Default { get; } = new(
        ExistingDistributionMarkers: Array.AsReadOnly(new[] { "releases", "jobs" }));

    internal string DirectoryFor(string area) => area switch
    {
        "catalog" => CatalogDirectory,
        "jobs" => JobsDirectory,
        "auth" => AuthDirectory,
        "sequence" => SequenceDirectory,
        AuthorityDocuments.Events => EventsDirectory,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    internal void Validate()
    {
        ReleaseSidecar.Segment(AuthorityDirectory);
        ReleaseSidecar.Relative(AuthorityDirectory);
        var directories = AuthorityDocuments.Areas.Select(area => DirectoryFor(AuthorityDocuments.Name(area)))
            .Concat([EventsDirectory, ReleaseMetadataDirectory, ProgressDirectory]).ToArray();
        foreach (var directory in directories)
        {
            ReleaseSidecar.Segment(directory);
            ReleaseSidecar.Relative(directory);
        }
        foreach (var marker in ExistingDistributionMarkers ?? []) ReleaseSidecar.Relative(marker);
        if (directories.Concat(["HEAD", "audit.jsonl"]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length + 2)
            throw new ArgumentException("Authority data directories must have distinct names.");
    }
}
