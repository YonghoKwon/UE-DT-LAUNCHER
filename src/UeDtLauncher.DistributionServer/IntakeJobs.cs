using System.Text.Json;
using System.Text.Json.Serialization;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

internal sealed record StoredIntakeJob(
    [property: JsonRequired] string Id, [property: JsonRequired] string Source,
    [property: JsonRequired] string State, [property: JsonRequired] string? Snapshot,
    [property: JsonRequired] string? Message, [property: JsonRequired] Guid? ClaimId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PublishedAt = null);

internal sealed record IntakeActiveWork(
    [property: JsonRequired] string Job, [property: JsonRequired] Guid Owner,
    [property: JsonRequired] Guid ClaimId, [property: JsonRequired] string? Scratch,
    [property: JsonRequired] string? Snapshot, [property: JsonRequired] DateTimeOffset StartedAt);

internal sealed record MaintenanceDeletion(
    [property: JsonRequired] string Id, [property: JsonRequired] string Path,
    [property: JsonRequired] string? Job, [property: JsonRequired] string TreeHash,
    [property: JsonRequired] DateTimeOffset At);

internal sealed record IntakeJobs(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] List<StoredIntakeJob> Jobs,
    [property: JsonRequired] List<IntakeActiveWork> ActiveWork,
    [property: JsonRequired] List<MaintenanceDeletion> MaintenanceDeletions)
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static IntakeJobs Read(AuthoritySnapshot snapshot)
    {
        var jobs = snapshot.Get(AuthorityArea.Jobs).Deserialize<IntakeJobs>(AuthorityDocuments.Json);
        if (jobs is null || jobs.Jobs is null || jobs.ActiveWork is null ||
            jobs.MaintenanceDeletions is null)
            throw new InvalidDataException("Invalid intake jobs area.");
        return jobs;
    }

    internal CommitPlan Plan(params AuthorityEvent[] events) =>
        new(new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Jobs] = JsonSerializer.SerializeToElement(this, AuthorityDocuments.Json)
        }, events);

    internal static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!PathComparer.Equals(Path.GetFullPath(path), Resolve(root, relative)))
            throw new InvalidDataException("Intake path does not round-trip under the distribution root.");
        return relative;
    }

    internal static string Resolve(string root, string relative)
    {
        if (relative != "." && (relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or "..")))
            throw new InvalidDataException("Intake path is not canonical relative to the distribution root.");
        // Incoming paths can contain a rejected upload link. Resolving the recorded name does not follow it.
        return SafePath.ResolveInside(root, relative);
    }

    internal static string? ResolveOptional(string root, string? path) =>
        path is null ? null : Resolve(root, path);
}
