using System.Text.Json;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Tests;

internal static class FileAuthorityFixtures
{
    internal static void SeedRelease(IntakeStore store, ReleaseSidecar metadata, string jobId)
    {
        metadata.PackageFile = "fixture.zip";
        metadata.PackageSize = 10;
        metadata.PackageSha256 = new string('0', 64);
        if (metadata.EntryPoint.Length == 0) metadata.EntryPoint = "game.bin";
        var job = new IntakeJob(jobId, Path.Combine(store.Root, "incoming", jobId), "pending", null, null);
        using var jobLock = store.LockJob(jobId);
        store.Save(job);
        using var claim = new IntakeWorkClaim(store, job, "publishing",
            Path.Combine(store.Root, "processing", jobId), null, "pending");
        store.Catalog.Publish(jobId, metadata, SidecarPackageValidator.SerializeForGeneration(metadata), claim,
            () => Directory.CreateDirectory(Path.Combine(store.Root, "releases", metadata.ReleaseId)));
    }

    internal static void SetSequence(IntakeStore store, object value) => store.Authority.Commit(_ =>
        new(new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Sequence] = JsonSerializer.SerializeToElement(new { schemaVersion = 1, highWater = value })
        }, []));

    internal static long HighWater(IntakeStore store) => CatalogSequenceAllocator.ReadHighWater(store.Authority.ReadSnapshot());

    internal static StoredAuthorityEvent[] Audit(IntakeStore store, string action) =>
        store.Authority.ReadAudit(store.Authority.ReadSnapshot()).Where(entry => entry.Action == action).ToArray();

    internal static void FailBeforeHead(AuthorityBoundary boundary)
    {
        if (boundary == AuthorityBoundary.BeforeHeadRename) throw new IOException("Injected authority commit failure.");
    }
}
