using Microsoft.Data.Sqlite;
using System.Text.Json;

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

    public PromotionSnapshot Snapshot()
    {
        using var gate = store.DatabaseRead(); using var db = store.Open(); using var transaction = db.BeginTransaction(deferred: true);
        var result = ReadSnapshot(db, transaction); transaction.Commit(); return result;
    }
    private static PromotionSnapshot ReadSnapshot(SqliteConnection db, SqliteTransaction transaction)
    {
        using var query = db.CreateCommand(); query.Transaction = transaction; query.CommandText = "SELECT ready FROM promotion_state WHERE id=1";
        var ready = Convert.ToInt32(query.ExecuteScalar() ?? throw new InvalidDataException("Missing promotion state.")) == 1;
        query.CommandText = "SELECT id,job,directory,metadata FROM releases ORDER BY rowid";
        var releases = new List<PublishedRelease>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) releases.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                JsonSerializer.Deserialize<ReleaseSidecar>(reader.GetString(3), JsonFiles.Options) ?? throw new InvalidDataException("Invalid publication metadata.")));
        query.CommandText = "SELECT id,track,release_id,previous_release,actor,at,reason,kind FROM promotions ORDER BY id";
        var events = new List<PromotionEvent>();
        using (var reader = query.ExecuteReader())
            while (reader.Read()) events.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return new(ready, releases, events);
    }

    public PromotionInspection Promote(ReleaseSelection selection, long expectedRevision, string reason)
    {
        var track = Track(selection);
        if (expectedRevision < 0 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2048 || reason.Contains('\0')) throw new ArgumentException("Expected revision and a bounded reason are required.");
        using var process = store.Lock(); using var gate = store.DatabaseWrite(); using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false);
        var snapshot = ReadSnapshot(db, tx);
        if (!snapshot.Ready) throw new InvalidOperationException("Promotion migration is required before promotion or serving.");
        var release = snapshot.Releases.SingleOrDefault(r => r.ReleaseId == selection.ReleaseId) ?? throw new InvalidOperationException("Only a published release in this exact track can be promoted.");
        var meta = release.Metadata;
        if (meta.ReleaseId != selection.ReleaseId) throw new InvalidDataException("Publication identity mismatch.");
        var current = snapshot.Events.LastOrDefault(e => e.Track == track);
        if ((current?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("Promotion revision changed; inspect again before retrying.");
        if (current?.ReleaseId != selection.ReleaseId)
        {
            Insert(db, tx, track, selection.ReleaseId, current?.ReleaseId, reason, "manual"); BeforeCommit.Value?.Invoke();
        }
        tx.Commit(); return Inspect(selection);
    }

    private static void Insert(SqliteConnection db, SqliteTransaction tx, string track, string release, string? previous, string reason, string kind)
    {
        var actor = RuntimeIdentities.Current().Owner; var now = DateTimeOffset.UtcNow.ToString("O");
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "INSERT INTO promotions(track,release_id,previous_release,actor,at,reason,kind) VALUES($track,$release,$previous,$actor,$at,$reason,$kind); INSERT INTO audit(at,action,job) VALUES($at,$action,$release)";
        query.Parameters.AddWithValue("$track", track); query.Parameters.AddWithValue("$release", release); query.Parameters.AddWithValue("$previous", (object?)previous ?? DBNull.Value);
        query.Parameters.AddWithValue("$actor", actor); query.Parameters.AddWithValue("$at", now); query.Parameters.AddWithValue("$reason", reason); query.Parameters.AddWithValue("$kind", kind);
        query.Parameters.AddWithValue("$action", kind == "manual" ? "release-promoted" : "promotion-legacy-baseline"); query.ExecuteNonQuery();
    }

    // This path is deliberately independent of IntakeStore construction: no mkdir, schema update, lock or backup.
    public static PromotionMigration PreviewMigration(DistributionSettings settings)
    {
        var path = Path.Combine(Path.GetFullPath(settings.Root), "distribution.db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); db.Open();
        using var tx = db.BeginTransaction(deferred: true); using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "PRAGMA user_version"; var schema = Convert.ToInt32(query.ExecuteScalar());
        if (schema is < 1 or > 4) throw new InvalidDataException("Unsupported migration source schema.");
        var ready = false;
        if (schema == 4) { query.CommandText = "SELECT ready FROM promotion_state WHERE id=1"; ready = Convert.ToInt32(query.ExecuteScalar()) == 1; }
        query.CommandText = "SELECT id FROM releases ORDER BY rowid"; var releases = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) releases.Add(reader.GetString(0));
        tx.Commit(); return new(ready, !ready, ready ? [] : releases);
    }

    public PromotionMigration Migrate()
    {
        using var offline = new AuthenticationProcessLease(store.Root); using var process = store.Lock(); using var gate = store.DatabaseWrite();
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: false); var snapshot = ReadSnapshot(db, tx);
        if (snapshot.Ready) { tx.Commit(); return new(true, false, []); }
        if (snapshot.Events.Count != 0) throw new InvalidDataException("Unready migration contains promotion events; inspect before proceeding.");
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var release in snapshot.Releases)
        {
            var m = release.Metadata; var track = Track(new(m.ProjectId, m.Environment, m.Channel, m.Platform, m.Version));
            Insert(db, tx, track, release.ReleaseId, last.GetValueOrDefault(track), "Preserve previous approval-order recommendations", "legacy-baseline"); last[track] = release.ReleaseId;
        }
        using var query = db.CreateCommand(); query.Transaction = tx; query.CommandText = "UPDATE promotion_state SET ready=1 WHERE id=1"; query.ExecuteNonQuery();
        BeforeCommit.Value?.Invoke(); tx.Commit(); return new(true, false, snapshot.Releases.Select(r => r.ReleaseId).ToArray());
    }
}
