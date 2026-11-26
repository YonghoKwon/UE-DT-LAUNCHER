using System.Text.Json;
using System.Text.Json.Serialization;
using UeDtLauncher.Distribution.Authority;

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
                var upper = ReserveAuthority(store.Authority);
                // Publish no in-memory number until this durable commit succeeds.
                afterReservationCommit?.Invoke(); // Internal persistence boundary only, never a CLI/environment option.
                next = upper - blockSize; end = upper;
            }
            return ++next;
        }
        finally { gate.Release(); }
    }

    private long ReserveAuthority(AuthorityStore authority)
    {
        long upper = 0;
        authority.Commit(snapshot =>
        {
            var highWater = ReadHighWater(snapshot);
            if (highWater > long.MaxValue - blockSize)
                throw new InvalidDataException("Catalog sequence is missing, invalid or exhausted.");
            upper = highWater + blockSize;
            return new(new Dictionary<AuthorityArea, JsonElement>
            {
                [AuthorityArea.Sequence] = JsonSerializer.SerializeToElement(new SequenceDocument(AuthorityDocuments.Schema, upper),
                    AuthorityDocuments.Json)
            }, []);
        });
        return upper;
    }

    internal static long ReadHighWater(AuthoritySnapshot snapshot)
    {
        SequenceDocument? sequence;
        try { sequence = snapshot.Get(AuthorityArea.Sequence).Deserialize<SequenceDocument>(AuthorityDocuments.Json); }
        catch (JsonException error) { throw new InvalidDataException("Catalog sequence format is invalid.", error); }
        if (sequence is null || sequence.HighWater < 0)
            throw new InvalidDataException("Catalog sequence is missing or invalid.");
        return sequence.HighWater;
    }

    private sealed record SequenceDocument(int SchemaVersion, [property: JsonRequired] long HighWater);
}
