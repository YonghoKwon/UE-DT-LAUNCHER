namespace UeDtLauncher.Distribution;

public sealed record PromotionEvent(long Revision, string Track, string ReleaseId, string? PreviousRelease,
    string Actor, string AtUtc, string Reason, string Kind);
public sealed record PromotionInspection(bool Ready, string Track, long Revision, string? RecommendedRelease, IReadOnlyList<PromotionEvent> History);
public sealed record PromotionMigration(bool Ready, bool ApplyRequired, IReadOnlyList<string> BaselineReleases);
public sealed record PromotionSnapshot(bool Ready, IReadOnlyList<PublishedRelease> Releases, IReadOnlyList<PromotionEvent> Events);

public sealed class ReleasePromotions(IntakeStore store)
{
    public static string Track(ReleaseSelection selection)
    {
        selection.Validate(); return string.Join('/', selection.ProjectId, selection.Environment, selection.Channel, selection.Platform);
    }
    internal static readonly AsyncLocal<Action?> BeforeCommit = new(); // Test seam, never configured by product commands.

    public PromotionInspection Inspect(ReleaseSelection selection)
    {
        var track = Track(selection); var snapshot = Snapshot();
        var history = snapshot.Events.Where(e => e.Track == track).ToArray(); var last = history.LastOrDefault();
        return new(snapshot.Ready, track, last?.Revision ?? 0, last?.ReleaseId, history);
    }

    public PromotionSnapshot Snapshot() => store.Catalog.Snapshot();

    public PromotionInspection Promote(ReleaseSelection selection, long expectedRevision, string reason)
    {
        var track = Track(selection);
        if (expectedRevision < 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2048 || reason.Contains('\0')) throw new ArgumentException("Expected revision and a bounded reason are required.");
        return store.Catalog.Promote(selection, track, expectedRevision, reason);
    }

    public static PromotionMigration PreviewMigration(DistributionSettings settings) => CatalogAuthority.InspectExisting(settings.Root);

    public PromotionMigration Migrate() => PreviewMigration(store.Settings);
}
