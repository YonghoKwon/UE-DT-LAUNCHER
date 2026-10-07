using Microsoft.Data.Sqlite;

namespace UeDtLauncher.Distribution;

/// <summary>Reserve a durable high-water mark before issuing any unique sequence.
/// One allocator belongs to the root's exclusive authentication-server lifetime.
/// Unused numbers are lost on restart, never replayed. Catalogs/authorization are not cached.</summary>
internal sealed class CatalogSequenceAllocator(IntakeStore store, int blockSize = 64, Action? afterReservationCommit = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long next, end;

    public async Task<long> NextAsync(CancellationToken token)
    {
        using (DistributionPerformance.MeasureSequenceWait()) await gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (next >= end)
            {
                if (blockSize is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(blockSize));
                using var write = store.DatabaseWrite();
                using var measurement = DistributionPerformance.MeasureDatabase("catalog-sequence");
                using var db = store.Open();
                using var transaction = db.BeginTransaction(deferred: false);
                using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE sequence SET value=value+$count WHERE id=1 AND typeof(value)='integer' AND value>=0 AND value<=$limit RETURNING value";
                command.Parameters.AddWithValue("$count", blockSize);
                command.Parameters.AddWithValue("$limit", long.MaxValue - blockSize);
                var upper = command.ExecuteScalar() is long value ? value : throw new InvalidDataException("Catalog sequence is missing, invalid or exhausted.");
                transaction.Commit(); // Publish no in-memory number until this durable commit succeeds.
                afterReservationCommit?.Invoke(); // Internal persistence boundary only, never a CLI/environment option.
                next = upper - blockSize; end = upper;
            }
            return ++next;
        }
        catch (SqliteException error) { DistributionPerformance.RecordDatabaseBusy(error.SqliteErrorCode); throw; }
        finally { gate.Release(); }
    }
}
