using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace UeDtLauncher.Distribution;

public sealed class AccessPolicy { public List<ClientAccess> Clients { get; set; } = new(); }
public sealed class ClientAccess
{
    public string Id { get; set; } = "";
    public List<string> Addresses { get; set; } = new();
    public List<ReleaseGrant> Grants { get; set; } = new();
}
public sealed class ReleaseGrant
{
    public string ProjectId { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Channel { get; set; } = "";
    public List<string> Versions { get; set; } = new();
    public bool Allows(ReleaseSidecar r) => ProjectId == r.ProjectId && Environment == r.Environment && Channel == r.Channel &&
        (Versions.Count == 0 || Versions.Contains(r.Version, StringComparer.Ordinal));
}
public interface IAccessPolicyProvider
{
    Task<AccessPolicy> LoadAsync(CancellationToken token);
    async Task<CompiledAccessPolicy> LoadCompiledAsync(CancellationToken token) =>
        CompiledAccessPolicy.Create(await LoadAsync(token));
}
public sealed class FileAccessPolicyProvider(string path) : IAccessPolicyProvider
{
    public const int MaxPolicyBytes = 4 * 1024 * 1024;
    private readonly object gate = new();
    private byte[]? previousBytes;
    private CompiledAccessPolicy? previousPolicy;

    public async Task<AccessPolicy> LoadAsync(CancellationToken token)
    {
        var bytes = await ReadBytesAsync(token);
        _ = Compile(bytes);
        // Preserve the provider's mutable DTO contract without exposing the shared compiled snapshot.
        return JsonSerializer.Deserialize<AccessPolicy>(bytes, JsonFiles.Options)!;
    }

    public async Task<CompiledAccessPolicy> LoadCompiledAsync(CancellationToken token) =>
        Compile(await ReadBytesAsync(token));

    private async Task<byte[]> ReadBytesAsync(CancellationToken token)
    {
        // Read on EVERY request, even when size/mtime match. No last-known-good authorization fallback.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxPolicyBytes) throw new InvalidDataException("Access policy exceeds size limit.");
        using var bytes = new MemoryStream((int)stream.Length);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + read > MaxPolicyBytes) throw new InvalidDataException("Access policy exceeds size limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private CompiledAccessPolicy Compile(byte[] bytes)
    {
        lock (gate)
        {
            if (previousBytes is not null && bytes.AsSpan().SequenceEqual(previousBytes))
            {
                DistributionPerformance.RecordPolicyReuse();
                return previousPolicy!;
            }
            var compiled = CompiledAccessPolicy.Create(JsonSerializer.Deserialize<AccessPolicy>(bytes, JsonFiles.Options)
                ?? throw new InvalidDataException("Access policy is null."));
            previousBytes = bytes;
            previousPolicy = compiled;
            DistributionPerformance.RecordPolicyCompilation();
            return compiled;
        }
    }
}

/// <summary>Immutable authorization inputs only; never caches a token, IP decision, or allowed release list.</summary>
public sealed class CompiledAccessPolicy
{
    private readonly FrozenDictionary<string, CompiledClientAccess> clients;
    private CompiledAccessPolicy(Dictionary<string, CompiledClientAccess> clients) =>
        this.clients = clients.ToFrozenDictionary(StringComparer.Ordinal);
    public CompiledClientAccess? FindClient(string id) => clients.GetValueOrDefault(id);

    public static CompiledAccessPolicy Create(AccessPolicy policy)
    {
        if (policy.Clients is null) throw new InvalidDataException("Missing policy clients.");
        var clients = new Dictionary<string, CompiledClientAccess>(StringComparer.Ordinal);
        foreach (var client in policy.Clients)
        {
            if (client is null || client.Id is null || client.Addresses is null || client.Grants is null)
                throw new InvalidDataException("Invalid policy client.");
            ReleaseSidecar.Segment(client.Id);
            if (!clients.TryAdd(client.Id, new CompiledClientAccess(client)))
                throw new InvalidDataException("Duplicate policy client IDs.");
        }
        return new CompiledAccessPolicy(clients);
    }
}

public sealed class CompiledClientAccess
{
    private readonly IPNetwork[] networks;
    private readonly Grant[] grants;
    private sealed record Grant(string ProjectId, string Environment, string Channel, FrozenSet<string> Versions)
    {
        public bool Allows(ReleaseSidecar r) => ProjectId == r.ProjectId && Environment == r.Environment &&
            Channel == r.Channel && (Versions.Count == 0 || Versions.Contains(r.Version));
    }
    internal CompiledClientAccess(ClientAccess client)
    {
        networks = client.Addresses.Select(value => value is null
            ? throw new InvalidDataException("Invalid policy address.") : AccessPolicyEvaluator.ParseNetwork(value)).ToArray();
        grants = client.Grants.Select(grant =>
        {
            if (grant is null || grant.ProjectId is null || grant.Environment is null || grant.Channel is null || grant.Versions is null)
                throw new InvalidDataException("Invalid policy grant.");
            ReleaseSidecar.Segment(grant.ProjectId);
            try { KnownValues.ValidateReleaseTuple("windows-x64", grant.Environment, grant.Channel); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid policy release tuple.", ex); }
            foreach (var version in grant.Versions)
            {
                if (version is null) throw new InvalidDataException("Invalid policy version.");
                ReleaseSidecar.Segment(version);
            }
            return new Grant(grant.ProjectId, grant.Environment, grant.Channel, grant.Versions.ToFrozenSet(StringComparer.Ordinal));
        }).ToArray();
    }
    public bool AllowsAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return networks.Any(network => network.Contains(address));
    }
    public bool AllowsRelease(ReleaseSidecar release) => grants.Any(grant => grant.Allows(release));
}
public static class AccessPolicyEvaluator
{
    public static IPNetwork ParseNetwork(string value) => value.Contains('/') ? IPNetwork.Parse(value) :
        new IPNetwork(IPAddress.Parse(value), IPAddress.Parse(value).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    public static bool Allows(ClientAccess client, IPAddress address, ReleaseSidecar release)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return client.Addresses.Any(cidr => ParseNetwork(cidr).Contains(address)) && client.Grants.Any(g => g.Allows(release));
    }
}
public sealed class DistributionTokens(IntakeStore store)
{
    public string Issue(string client)
    {
        ReleaseSidecar.Segment(client); using var gate = store.Lock();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var database = store.DatabaseWrite();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO tokens(hash,client) VALUES($hash,$client)";
        command.Parameters.AddWithValue("$hash", Hash(token)); command.Parameters.AddWithValue("$client", client);
        command.ExecuteNonQuery(); store.Audit("token-issued", client); return token;
    }
    public void Revoke(string client)
    {
        using var gate = store.Lock(); using var database = store.DatabaseWrite();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE tokens SET revoked=1 WHERE client=$client";
        command.Parameters.AddWithValue("$client", client); command.ExecuteNonQuery(); store.Audit("tokens-revoked", client);
    }
    public string? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return null;
        using var measurement = DistributionPerformance.MeasureDatabase("token-authenticate");
        using var database = store.DatabaseRead();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT client FROM tokens WHERE hash=$hash AND revoked=0";
        command.Parameters.AddWithValue("$hash", Hash(token));
        try { return command.ExecuteScalar() as string; }
        catch (SqliteException ex) { DistributionPerformance.RecordDatabaseBusy(ex.SqliteErrorCode); throw; }
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
