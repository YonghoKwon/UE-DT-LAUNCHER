using Microsoft.Data.Sqlite;

namespace UeDtLauncher.Distribution;

/// <summary>A durable ownership record, valid only while the caller holds the non-expiring OS job lock.</summary>
internal sealed class IntakeWorkClaim : IDisposable
{
    private readonly IntakeStore store;
    private readonly string jobId;
    private readonly string owner = Guid.NewGuid().ToString("N");

    public IntakeWorkClaim(IntakeStore store, IntakeJob job, string state, string scratch, string? snapshot,
        params string[] expectedStates)
    {
        this.store = store; jobId = job.Id;
        using var gate = store.Lock();
        using var database = store.DatabaseWrite();
        using var db = store.Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        var parameters = expectedStates.Select((_, i) => "$state" + i).ToArray();
        command.CommandText = "UPDATE jobs SET state=$next,message=NULL WHERE id=$id AND state IN (" + string.Join(',', parameters) + ")";
        command.Parameters.AddWithValue("$next", state); command.Parameters.AddWithValue("$id", job.Id);
        for (var i = 0; i < parameters.Length; i++) command.Parameters.AddWithValue(parameters[i], expectedStates[i]);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Job state changed before claim.");
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO active_work(job,owner,scratch,snapshot,started_at) VALUES($id,$owner,$scratch,$snapshot,$at)
            ON CONFLICT(job) DO UPDATE SET owner=$owner,scratch=$scratch,snapshot=$snapshot,started_at=$at;
            INSERT INTO audit(at,action,job) VALUES($at,$action,$id);
            """;
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$scratch", scratch); command.Parameters.AddWithValue("$snapshot", (object?)snapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$action", state);
        command.ExecuteNonQuery(); transaction.Commit();
    }

    public void Dispose()
    {
        using var gate = store.Lock(); using var database = store.DatabaseWrite();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM active_work WHERE job=$id AND owner=$owner";
        command.Parameters.AddWithValue("$id", jobId); command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }
}
