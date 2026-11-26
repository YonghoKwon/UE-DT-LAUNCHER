using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

/// <summary>A durable ownership record, valid only while the caller holds the non-expiring OS job lock.</summary>
internal sealed class IntakeWorkClaim : IDisposable
{
    private readonly IntakeStore store;
    private readonly string jobId;
    private readonly Guid owner = Guid.NewGuid();
    private bool published;

    internal Guid ClaimId { get; } = Guid.NewGuid();

    public IntakeWorkClaim(IntakeStore store, IntakeJob job, string state, string scratch, string? snapshot,
        params string[] expectedStates)
    {
        this.store = store;
        jobId = job.Id;
        store.Authority.Commit(current =>
        {
            var jobs = IntakeJobs.Read(current);
            var index = jobs.Jobs.FindIndex(candidate => candidate.Id == jobId && expectedStates.Contains(candidate.State));
            if (index < 0) throw new InvalidOperationException("Job state changed before claim.");
            var now = DateTimeOffset.UtcNow;
            jobs.Jobs[index] = jobs.Jobs[index] with { State = state, Message = null, ClaimId = ClaimId };
            jobs.ActiveWork.RemoveAll(work => work.Job == jobId);
            jobs.ActiveWork.Add(new(jobId, owner, ClaimId, IntakeJobs.Relative(store.Root, scratch),
                snapshot is null ? null : IntakeJobs.Relative(store.Root, snapshot), now));
            return jobs.Plan(new AuthorityEvent(now, state, jobId));
        });
    }

    internal void CompletePublication(IntakeJobs jobs, DateTimeOffset completedAt)
    {
        var index = PublishingIndex(jobs);
        jobs.Jobs[index] = jobs.Jobs[index] with { State = "published", ClaimId = null, PublishedAt = completedAt };
        jobs.ActiveWork.RemoveAll(work => work.Job == jobId && work.Owner == owner);
    }

    internal void RejectConflictingPublication(IntakeJobs jobs)
    {
        var index = PublishingIndex(jobs);
        jobs.Jobs[index] = jobs.Jobs[index] with { State = "failed", Message = ApprovedPublisher.PublicationConflictMessage };
    }

    private int PublishingIndex(IntakeJobs jobs)
    {
        var index = jobs.Jobs.FindIndex(job => job.Id == jobId && job.State == "publishing");
        if (index < 0 || !jobs.ActiveWork.Exists(work =>
            work.Job == jobId && work.Owner == owner && work.ClaimId == jobs.Jobs[index].ClaimId))
            throw new InvalidOperationException("Publication claim no longer owns the job.");
        return index;
    }

    internal void MarkPublished() => published = true;

    public void Dispose()
    {
        if (published) return;
        store.Authority.Commit(snapshot =>
        {
            var jobs = IntakeJobs.Read(snapshot);
            return jobs.ActiveWork.RemoveAll(work => work.Job == jobId && work.Owner == owner) == 0
                ? CommitPlan.Audit() : jobs.Plan();
        });
    }
}
