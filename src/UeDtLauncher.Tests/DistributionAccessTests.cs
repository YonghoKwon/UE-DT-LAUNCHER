using System.Net;
using UeDtLauncher.Distribution;
using Microsoft.Data.Sqlite;
using Xunit;

namespace UeDtLauncher.Tests;
public class DistributionAccessTests
{
    [Fact]
    public void PolicyRequiresIpProjectTrackAndOptionalVersion()
    {
        var client = new ClientAccess { Id = "pc", Addresses = { "10.2.3.0/24" }, Grants =
        { new ReleaseGrant { ProjectId = "demo", Environment = "prod", Channel = "stable", Versions = { "1.0.0" } } } };
        var release = new ReleaseSidecar { ProjectId = "demo", Version = "1.0.0" };
        Assert.True(AccessPolicyEvaluator.Allows(client, IPAddress.Parse("10.2.3.4"), release));
        Assert.False(AccessPolicyEvaluator.Allows(client, IPAddress.Parse("10.2.4.4"), release));
        release.Environment = "dev";
        Assert.False(AccessPolicyEvaluator.Allows(client, IPAddress.Parse("10.2.3.4"), release));
        release.Environment = "prod"; release.Version = "2.0.0";
        Assert.False(AccessPolicyEvaluator.Allows(client, IPAddress.Parse("10.2.3.4"), release));
        Assert.False(AccessPolicyEvaluator.Allows(new ClientAccess(), IPAddress.Loopback, release));
    }
    [Fact]
    public void TokenIsHashedAndRevokedTokensFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "access-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new IntakeStore(new DistributionSettings { Root = root });
            var tokens = new DistributionTokens(store); var token = tokens.Issue("pc");
            Assert.Equal("pc", tokens.Authenticate(token));
            Assert.Null(tokens.Authenticate(token + "bad"));
            tokens.Revoke("pc"); Assert.Null(tokens.Authenticate(token));
            using var db = store.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT hash FROM tokens";
            Assert.NotEqual(token, command.ExecuteScalar());
            command.CommandText = "SELECT sqlite_version()";
            Assert.True(Version.Parse((string)command.ExecuteScalar()!) >= new Version(3, 50, 2));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
