using System.Text.Json;
using System.Text.Json.Serialization;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

public sealed partial class IntakeStore
{
    public List<IntakeJob> List() =>
        IntakeJobs.Read(Authority.ReadSnapshot()).Jobs.Select(Present).ToList();

    public IntakeJob? Find(string id)
    {
        var job = IntakeJobs.Read(Authority.ReadSnapshot()).Jobs.Find(job => job.Id == id);
        return job is null ? null : Present(job);
    }

    public IntakeJob Get(string id) => Find(id) ?? throw new InvalidOperationException("Job not found.");

    private IntakeJob Present(StoredIntakeJob job)
    {
        var progress = ReadProgress(job);
        return new(job.Id, IntakeJobs.Resolve(Root, job.Source), job.State,
            IntakeJobs.ResolveOptional(Root, job.Snapshot), job.Message,
            progress?.Phase, progress?.ProcessedBytes ?? 0, progress?.TotalBytes ?? 0,
            progress?.EstimatedAdditionalDiskBytes ?? 0, progress?.UpdatedAt);
    }

    public void Save(IntakeJob job) => SaveJob(job, resetClaim: false);

    private void SaveJob(IntakeJob job, bool resetClaim)
    {
        ReleaseSidecar.Segment(job.Id);
        var hasProgress = job.Phase is not null || job.ProgressUpdatedAt is not null ||
            job.ProcessedBytes != 0 || job.TotalBytes != 0 || job.EstimatedAdditionalDiskBytes != 0;
        StoredProgress? savedProgress = null;
        using var writer = LockWriter();
        Authority.CommitUnderHeldLock(writer, snapshot =>
        {
            var jobs = IntakeJobs.Read(snapshot);
            var index = jobs.Jobs.FindIndex(current => current.Id == job.Id);
            var previous = index < 0 ? null : jobs.Jobs[index];
            var claim = resetClaim ? null : previous?.ClaimId;
            // A direct Save can supply display progress without starting an active job.
            if (!resetClaim && hasProgress && claim is null) claim = Guid.NewGuid();
            if (claim is { } binding)
                savedProgress = new(job.Id, binding, job.Phase, job.ProcessedBytes, job.TotalBytes,
                    job.EstimatedAdditionalDiskBytes, job.ProgressUpdatedAt);
            var next = new StoredIntakeJob(job.Id, previous?.Source ?? IntakeJobs.Relative(Root, job.Source),
                job.State, job.Snapshot is null ? null : IntakeJobs.Relative(Root, job.Snapshot), job.Message,
                claim);
            if (index < 0) jobs.Jobs.Add(next);
            else jobs.Jobs[index] = next;
            return jobs.Plan(new AuthorityEvent(DateTimeOffset.UtcNow, job.State, job.Id));
        });
        if (savedProgress is not null) WriteProgress(savedProgress);
    }

    public IDisposable Lock() => LockWriter();
    internal AuthorityStore.WriterLease LockWriter() => Authority.Lock();

    public IDisposable LockJob(string id)
    {
        ReleaseSidecar.Segment(id);
        // A stable path and a single, nonblocking OS acquisition protect the job across processes.
        return new FileStream(SafePath.ResolveInsideChecked(Root, ".job-locks/" + id + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal bool ChangeState(string id, string expected, string next, bool reset = false, string? message = null,
        IDisposable? writer = null, bool recordAudit = true)
    {
        CommitPlan Plan(AuthoritySnapshot snapshot)
        {
            var jobs = IntakeJobs.Read(snapshot);
            var index = jobs.Jobs.FindIndex(job => job.Id == id && job.State == expected);
            if (index < 0) return CommitPlan.Audit();
            var job = jobs.Jobs[index] with { State = next, Message = message };
            if (reset) job = job with { Snapshot = null, ClaimId = null };
            jobs.Jobs[index] = job;
            return recordAudit ? jobs.Plan(new AuthorityEvent(DateTimeOffset.UtcNow, next, id)) : jobs.Plan();
        }

        return (writer is null ? Authority.Commit(Plan)
            : Authority.CommitUnderHeldLock((AuthorityStore.WriterLease)writer, Plan)).Changed;
    }

    public void Audit(string action, string subject) =>
        Authority.Commit(_ => CommitPlan.Audit(new AuthorityEvent(DateTimeOffset.UtcNow, action, subject)));

    internal Func<string, long>? AvailableBytes => availableBytes;

    private sealed record StoredProgress(
        [property: JsonRequired] string Job,
        [property: JsonRequired] Guid ClaimId,
        [property: JsonRequired] string? Phase,
        [property: JsonRequired] long ProcessedBytes,
        [property: JsonRequired] long TotalBytes,
        [property: JsonRequired] long EstimatedAdditionalDiskBytes,
        [property: JsonRequired] string? UpdatedAt);

    internal ThrottledPackageProgress Progress(string id, IProgress<PackageWorkProgress>? observer = null)
    {
        var claimId = IntakeJobs.Read(Authority.ReadSnapshot()).Jobs.Find(job => job.Id == id)?.ClaimId;
        return new(value =>
        {
            if (claimId is { } claim)
            {
                using var writer = LockWriter();
                var current = IntakeJobs.Read(Authority.ReadSnapshot()).Jobs.Find(job => job.Id == id);
                if (current?.ClaimId == claim)
                    WriteProgress(new(id, claim, value.Phase, value.ProcessedBytes, value.TotalBytes,
                        value.EstimatedAdditionalDiskBytes, DateTimeOffset.UtcNow.ToString("O")));
            }
            observer?.Report(value);
        });
    }

    private string ProgressFile(string id, Guid claim)
    {
        ReleaseSidecar.Segment(id);
        return SafePath.ResolveInsideChecked(Authority.ProgressPath, $"{id}.{claim:N}.json");
    }

    private StoredProgress? ReadProgress(StoredIntakeJob job)
    {
        if (job.ClaimId is not { } claim) return null;
        try
        {
            using var stream = AuthorityDisk.OpenReader(ProgressFile(job.Id, claim));
            if (stream.Length > 65536) return null;
            var progress = JsonSerializer.Deserialize<StoredProgress>(stream, AuthorityDocuments.Json);
            return progress is not null && progress.Job == job.Id && progress.ClaimId == claim ? progress : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null; // The job remains authoritative even if its advisory progress is missing or damaged.
        }
    }

    private void WriteProgress(StoredProgress progress)
    {
        try
        {
            var path = ProgressFile(progress.Job, progress.ClaimId);
            var temporary = AuthorityDisk.Temporary(path);
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(output, progress, AuthorityDocuments.Json);
                AuthorityDisk.Rename(temporary, path, replace: true);
            }
            finally { AuthorityDisk.TryDelete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An unavailable progress file must not fail package work or change its authoritative state.
        }
    }
}
