using Microsoft.Data.Sqlite;

namespace UeDtLauncher.Distribution;

public sealed record CredentialMetadata(string Id, string Client, bool Revoked, string? ExpiresAtUtc, bool Expired);

internal static class CredentialLifecycle
{
    internal static string? Expiry(DateTimeOffset? expiry) => expiry?.ToUniversalTime().ToString("O");
    internal static void Audit(SqliteConnection db, SqliteTransaction tx, string action, string id)
    {
        using var q = db.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO audit(at,action,job) VALUES($at,$action,$id)";
        q.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); q.Parameters.AddWithValue("$action", action); q.Parameters.AddWithValue("$id", id); q.ExecuteNonQuery();
    }
    internal static bool Due(string? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is null) return false;
        if (!DateTimeOffset.TryParseExact(expiresAt, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var expiry)) throw new InvalidDataException("Invalid credential expiry.");
        return now >= expiry;
    }
    internal static void LatchExpiry(IntakeStore store, bool deviceKey, string id)
    {
        using var write = store.DatabaseWrite(); using var db = store.Open(); using var tx = db.BeginTransaction();
        using var q = db.CreateCommand(); q.Transaction = tx;
        q.CommandText = deviceKey ? "UPDATE device_keys SET expired=1 WHERE key_id=$id AND expired=0" : "UPDATE tokens SET expired=1 WHERE management_id=$id AND expired=0";
        q.Parameters.AddWithValue("$id", id);
        if (q.ExecuteNonQuery() == 1) Audit(db, tx, "credential-expired", id);
        tx.Commit();
    }
}
