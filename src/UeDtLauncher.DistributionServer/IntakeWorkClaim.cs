using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

/// <summary>A durable ownership record, valid only while the caller holds the non-expiring OS job lock.</summary>
internal sealed class IntakeWorkClaim : IDisposable
{
    private readonly IntakeStore store;
    private readonly string jobId;
    private readonly Guid owner = Guid.NewGuid();

    public IntakeWorkClaim(IntakeStore store, IntakeJob job, string state, string scratch, string? snapshot,
        params string[] expectedStates)
    {
        this.store = store;
        jobId = job.Id;
        var claimId = Guid.NewGuid();
        store.Authority.Commit(current =>
        {
            var jobs = IntakeJobs.Read(current);
            var index = jobs.Jobs.FindIndex(candidate => candidate.Id == jobId && expectedStates.Contains(candidate.State));
            if (index < 0) throw new InvalidOperationException("Job state changed before claim.");
            var now = DateTimeOffset.UtcNow;
            jobs.Jobs[index] = jobs.Jobs[index] with { State = state, Message = null, ClaimId = claimId };
            jobs.ActiveWork.RemoveAll(work => work.Job == jobId);
            jobs.ActiveWork.Add(new(jobId, owner, claimId, IntakeJobs.Relative(store.Root, scratch),
                snapshot is null ? null : IntakeJobs.Relative(store.Root, snapshot), now));
            return jobs.Plan(new AuthorityEvent(now, state, jobId));
        });
    }

    public void Dispose() => store.Authority.Commit(snapshot =>
    {
        var jobs = IntakeJobs.Read(snapshot);
        return jobs.ActiveWork.RemoveAll(work => work.Job == jobId && work.Owner == owner) == 0
            ? CommitPlan.Audit() : jobs.Plan();
    });
}
