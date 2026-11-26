using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution.Authority;

namespace UeDtLauncher.Distribution;

internal sealed class CredentialAuthority(AuthorityStore store)
{
    // Temporary integration seam. Bind once before handing the IntakeStore to any credential caller.
    private static readonly ConditionalWeakTable<IntakeStore, CredentialAuthority> Bindings = new();

    internal static void Bind(IntakeStore intake, AuthorityStore authority) => Bindings.Add(intake, new(authority));

    internal static CredentialAuthority? For(IntakeStore intake) => Bindings.TryGetValue(intake, out var authority) ? authority : null;

    internal static CredentialMetadata? FindToken(IntakeStore intake, string hash)
    {
        if (For(intake) is { } authority)
            return authority.Read().Tokens.Find(token => token.Hash == hash)?.Metadata;

        using var measurement = DistributionPerformance.MeasureDatabase("token-authenticate");
        using var database = intake.DatabaseRead();
        using var db = intake.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT management_id,client,revoked,expires_at,expired FROM tokens WHERE hash=$hash";
        command.Parameters.AddWithValue("$hash", hash);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4))
            : null;
    }

    internal static RegisteredDeviceKey? FindDeviceKey(IntakeStore intake, string keyId, Func<SqliteDataReader, RegisteredDeviceKey> read)
    {
        if (For(intake) is { } authority)
            return authority.Read().DeviceKeys.Find(key => key.KeyId == keyId)?.Registration;

        using var database = intake.DatabaseRead();
        using var db = intake.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT key_id,client,public_pem,revoked,expires_at,expired FROM device_keys WHERE key_id=$key";
        command.Parameters.AddWithValue("$key", keyId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? read(reader) : null;
    }

    internal IReadOnlyList<CredentialMetadata> ListTokens() => Read().Tokens.Select(token => token.Metadata).ToArray();

    internal IReadOnlyList<RegisteredDeviceKey> ListDeviceKeys() => Read().DeviceKeys
        .OrderBy(key => key.KeyId, StringComparer.Ordinal).Select(key => key.Registration).ToArray();

    internal void IssueToken(string hash, string client, string? expiry)
    {
        var id = Guid.NewGuid().ToString("N");
        Change(document =>
        {
            if (document.Tokens.Exists(token => token.Hash == hash || token.Id == id))
                throw new InvalidDataException("Token identity already exists.");
            document.Tokens.Add(new(hash, id, client, false, expiry, false));
            return [Event("token-issued", id)];
        });
    }

    internal void RevokeClientTokens(string client) => Change(document =>
    {
        for (var index = 0; index < document.Tokens.Count; index++)
        {
            var token = document.Tokens[index];
            if (token.Client == client) document.Tokens[index] = token with { Revoked = true };
        }
        return [Event("tokens-revoked", client)];
    });

    internal void RevokeToken(string id) => Change(document =>
    {
        var index = document.Tokens.FindIndex(token => token.Id == id);
        if (index < 0) throw new InvalidDataException("Unknown token management ID.");
        document.Tokens[index] = document.Tokens[index] with { Revoked = true };
        return [Event("token-revoked", id)];
    });

    internal void AddDeviceKey(string client, DevicePublicKey key, string? expiry) => Change(document =>
    {
        if (document.DeviceKeys.Exists(existing => existing.KeyId == key.KeyId))
            throw new InvalidDataException("Device key already exists.");
        document.DeviceKeys.Add(new(key.KeyId, client, key.PublicKeyPem, false, expiry, false));
        return [Event("device-key-added", key.KeyId)];
    });

    internal void AddDeviceKeys(IEnumerable<(string Client, DevicePublicKey Key, DateTimeOffset? Expiry)> registrations,
        AuthorityStore.WriterLease? writer = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var candidates = new List<DeviceEntry>();
        foreach (var (client, key, expiry) in registrations)
        {
            ReleaseSidecar.Segment(client);
            ArgumentNullException.ThrowIfNull(key);
            DeviceCredentials.ValidatePublic(key);
            candidates.Add(new(key.KeyId, client, key.PublicKeyPem, false, CredentialLifecycle.Expiry(expiry), false));
        }
        if (candidates.Count == 0) return;

        Change(document =>
        {
            var keys = document.DeviceKeys.ToDictionary(key => key.KeyId, StringComparer.Ordinal);
            var events = new List<AuthorityEvent>();
            foreach (var candidate in candidates)
            {
                if (keys.TryGetValue(candidate.KeyId, out var existing))
                {
                    if (existing.ClientId != candidate.ClientId || existing.PublicKeyPem != candidate.PublicKeyPem)
                        throw new InvalidDataException("Device key registration conflicts with an existing key.");
                    continue;
                }
                keys.Add(candidate.KeyId, candidate);
                document.DeviceKeys.Add(candidate);
                events.Add(Event("device-key-added", candidate.KeyId));
            }
            return events;
        }, writer);
    }

    internal void RevokeDeviceKey(string keyId) => Change(document =>
    {
        var index = document.DeviceKeys.FindIndex(key => key.KeyId == keyId);
        if (index < 0) throw new InvalidDataException("Unknown device key.");
        document.DeviceKeys[index] = document.DeviceKeys[index] with { Revoked = true };
        return [Event("device-key-revoked", keyId)];
    });

    internal void LatchExpiry(bool deviceKey, string id) => Change(document =>
    {
        if (deviceKey)
        {
            var index = document.DeviceKeys.FindIndex(key => key.KeyId == id && !key.Expired);
            if (index < 0) return [];
            document.DeviceKeys[index] = document.DeviceKeys[index] with { Expired = true };
        }
        else
        {
            var index = document.Tokens.FindIndex(token => token.Id == id && !token.Expired);
            if (index < 0) return [];
            document.Tokens[index] = document.Tokens[index] with { Expired = true };
        }
        return [Event("credential-expired", id)];
    });

    private static AuthorityEvent Event(string action, string id) => new(DateTimeOffset.UtcNow, action, id);

    private void Change(Func<AuthDocument, IReadOnlyList<AuthorityEvent>> update, AuthorityStore.WriterLease? writer = null)
    {
        CommitPlan Plan(AuthoritySnapshot snapshot)
        {
            var document = Read(snapshot);
            var events = update(document);
            return events.Count == 0 ? CommitPlan.Audit() : new(
                new Dictionary<AuthorityArea, JsonElement>
                {
                    [AuthorityArea.Auth] = JsonSerializer.SerializeToElement(document, AuthorityDocuments.Json)
                },
                events);
        }

        if (writer is null) store.Commit(Plan);
        else store.CommitUnderHeldLock(writer, Plan);
    }

    private AuthDocument Read() => Read(store.ReadSnapshot());

    private static AuthDocument Read(AuthoritySnapshot snapshot)
    {
        try
        {
            var json = snapshot.Get(AuthorityArea.Auth);
            CheckRows(json.GetProperty("tokens"));
            CheckRows(json.GetProperty("deviceKeys"));
            var document = json.Deserialize<AuthDocument>(AuthorityDocuments.Json) ?? throw new JsonException();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in document.Tokens)
                if (string.IsNullOrEmpty(token.Client) || string.IsNullOrEmpty(token.Id) ||
                    string.IsNullOrEmpty(token.Hash) || !ids.Add(token.Id) || !hashes.Add(token.Hash))
                    throw new JsonException();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in document.DeviceKeys)
                if (string.IsNullOrEmpty(key.ClientId) || string.IsNullOrEmpty(key.KeyId) ||
                    string.IsNullOrEmpty(key.PublicKeyPem) || !keys.Add(key.KeyId))
                    throw new JsonException();
            return document;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AuthorityStoreException("Invalid credential authority document.", error);
        }
    }

    private static void CheckRows(JsonElement rows)
    {
        foreach (var row in rows.EnumerateArray())
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in row.EnumerateObject())
                if (!fields.Add(property.Name)) throw new JsonException();
        }
    }

    private sealed record AuthDocument(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] List<TokenEntry> Tokens,
        [property: JsonRequired] List<DeviceEntry> DeviceKeys);

    private sealed record TokenEntry(
        [property: JsonRequired] string Hash,
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Client,
        [property: JsonRequired] bool Revoked,
        [property: JsonRequired] string? ExpiresAtUtc,
        [property: JsonRequired] bool Expired)
    {
        [JsonIgnore]
        internal CredentialMetadata Metadata => new(Id, Client, Revoked, ExpiresAtUtc, Expired);
    }

    private sealed record DeviceEntry(
        [property: JsonRequired] string KeyId,
        [property: JsonRequired] string ClientId,
        [property: JsonRequired] string PublicKeyPem,
        [property: JsonRequired] bool Revoked,
        [property: JsonRequired] string? ExpiresAtUtc,
        [property: JsonRequired] bool Expired)
    {
        [JsonIgnore]
        internal RegisteredDeviceKey Registration => new(KeyId, ClientId, PublicKeyPem, Revoked, ExpiresAtUtc, Expired);
    }
}
