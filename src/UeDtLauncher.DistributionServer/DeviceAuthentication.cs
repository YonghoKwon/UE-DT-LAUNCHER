using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public sealed record RegisteredDeviceKey(string KeyId, string ClientId, string PublicKeyPem, bool Revoked, string? ExpiresAtUtc = null, bool Expired = false);

internal sealed class AuthenticationProcessLease : IDisposable
{
    private readonly FileStream stream;
    public AuthenticationProcessLease(string root) => stream = new FileStream(Path.Combine(root, ".http-auth.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void Dispose() => stream.Dispose(); // Keep the lock inode: never unlink an active/reusable OS lock.
}

public sealed class DistributionDeviceKeys(IntakeStore store, TimeProvider? clock = null)
{
    public void Add(string client, DevicePublicKey key, DateTimeOffset? expiresAt = null)
    {
        ReleaseSidecar.Segment(client); DeviceCredentials.ValidatePublic(key);
        store.Credentials.AddDeviceKey(client, key, CredentialLifecycle.Expiry(expiresAt));
    }
    public void AddMany(IEnumerable<(string Client, DevicePublicKey Key, DateTimeOffset? Expiry)> registrations) =>
        store.Credentials.AddDeviceKeys(registrations);

    public void Revoke(string keyId)
    {
        DeviceCredentials.ValidateIdentifier(keyId);
        store.Credentials.RevokeDeviceKey(keyId);
    }
    public RegisteredDeviceKey? Find(string keyId)
    {
        var found = store.Credentials.FindDeviceKey(keyId);
        if (found is null || found.Revoked || found.Expired) return found is null ? null : found with { Revoked = true };
        if (!CredentialLifecycle.Due(found.ExpiresAtUtc, (clock ?? TimeProvider.System).GetUtcNow())) return found;
        CredentialLifecycle.LatchExpiry(store, true, keyId); return found with { Revoked = true, Expired = true };
    }
    public IReadOnlyList<RegisteredDeviceKey> List() => store.Credentials.ListDeviceKeys();
}

public sealed class DeviceAuthenticationState : IDisposable
{
    private sealed record ChallengePayload(string Origin, string KeyId, long Deadline);
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider clock;
    private readonly string origin;
    private readonly int maxNonces, maxPerKey;
    private readonly object gate = new();
    private readonly Dictionary<string, long> nonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> keyCounts = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(string Nonce, string Key), long> expirations = new();
    private readonly Dictionary<string, (long Window, int Count)> rate = new(StringComparer.Ordinal);
    private long rateWindow;
    private int rateCount;

    public DeviceAuthenticationState(string origin, TimeProvider? clock = null, int maxNonces = 100_000, int maxPerKey = 10_000)
    { this.origin = origin; this.clock = clock ?? TimeProvider.System; this.maxNonces = maxNonces; this.maxPerKey = maxPerKey; }

    public bool AllowChallenge(string address)
    {
        lock (gate)
        {
            var window = clock.GetTimestamp() / clock.TimestampFrequency;
            if (window != rateWindow) { rate.Clear(); rateCount = 0; rateWindow = window; }
            if (rateCount >= 100 || (rate.TryGetValue(address, out var current) && current.Count >= 30)) return false;
            rateCount++; rate[address] = (window, current.Count + 1); return true;
        }
    }

    public string Issue(string keyId)
    {
        DeviceCredentials.ValidateIdentifier(keyId);
        var payload = RequestSignatures.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new ChallengePayload(origin, keyId, checked(clock.GetTimestamp() + clock.TimestampFrequency * 60))));
        return payload + "." + RequestSignatures.Base64Url(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(payload)));
    }

    public bool TryValidate(string token, string keyId, out long deadline)
    {
        deadline = 0;
        try
        {
            if (token.Length > 2048) return false;
            var parts = token.Split('.'); if (parts.Length != 2) return false;
            var expected = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(expected, RequestSignatures.FromBase64Url(parts[1]))) return false;
            var value = JsonSerializer.Deserialize<ChallengePayload>(RequestSignatures.FromBase64Url(parts[0]));
            if (value is null || value.Origin != origin || value.KeyId != keyId || value.Deadline <= clock.GetTimestamp()) return false;
            deadline = value.Deadline; return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }

    public string? Consume(string keyId, string nonce, long deadline)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            while (expirations.TryPeek(out var item, out var expiry) && expiry <= now)
            {
                expirations.Dequeue(); nonces.Remove(item.Nonce);
                if (--keyCounts[item.Key] == 0) keyCounts.Remove(item.Key);
            }
            if (deadline <= now) return "stale-challenge";
            var id = keyId + ":" + nonce;
            if (nonces.ContainsKey(id)) return "replayed-request";
            var count = keyCounts.GetValueOrDefault(keyId);
            if (nonces.Count >= maxNonces || count >= maxPerKey) return "auth-capacity";
            nonces.Add(id, deadline); keyCounts[keyId] = count + 1; expirations.Enqueue((id, keyId), deadline);
            return null;
        }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(secret);
}
