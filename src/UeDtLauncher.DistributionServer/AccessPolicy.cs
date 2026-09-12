using System.Net;
using System.Security.Cryptography;
using System.Text;

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
public interface IAccessPolicyProvider { Task<AccessPolicy> LoadAsync(CancellationToken token); }
public sealed class FileAccessPolicyProvider(string path) : IAccessPolicyProvider
{
    public async Task<AccessPolicy> LoadAsync(CancellationToken token)
    {
        var policy = await JsonFiles.ReadAsync<AccessPolicy>(path, token);
        if (policy.Clients.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != policy.Clients.Count)
            throw new InvalidDataException("Duplicate policy client IDs.");
        foreach (var client in policy.Clients)
        {
            ReleaseSidecar.Segment(client.Id);
            foreach (var address in client.Addresses) _ = AccessPolicyEvaluator.ParseNetwork(address);
            foreach (var grant in client.Grants)
            {
                ReleaseSidecar.Segment(grant.ProjectId);
                KnownValues.ValidateReleaseTuple("windows-x64", grant.Environment, grant.Channel);
                foreach (var version in grant.Versions) ReleaseSidecar.Segment(version);
            }
        }
        return policy;
    }
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
    private void Initialize()
    {
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS tokens(hash TEXT PRIMARY KEY,client TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0)";
        command.ExecuteNonQuery();
    }
    public string Issue(string client)
    {
        ReleaseSidecar.Segment(client); Initialize(); using var gate = store.Lock();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO tokens(hash,client) VALUES($hash,$client)";
        command.Parameters.AddWithValue("$hash", Hash(token)); command.Parameters.AddWithValue("$client", client);
        command.ExecuteNonQuery(); store.Audit("token-issued", client); return token;
    }
    public void Revoke(string client)
    {
        Initialize(); using var gate = store.Lock(); using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "UPDATE tokens SET revoked=1 WHERE client=$client";
        command.Parameters.AddWithValue("$client", client); command.ExecuteNonQuery(); store.Audit("tokens-revoked", client);
    }
    public string? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 1024) return null;
        Initialize(); using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT client FROM tokens WHERE hash=$hash AND revoked=0";
        command.Parameters.AddWithValue("$hash", Hash(token)); return command.ExecuteScalar() as string;
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
