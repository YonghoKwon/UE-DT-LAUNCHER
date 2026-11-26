using System.Net;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
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
        var root = Directory.CreateTempSubdirectory("access-test-").FullName;
        try
        {
            var store = new IntakeStore(new DistributionSettings { Root = root });
            var tokens = new DistributionTokens(store); var token = tokens.Issue("pc");
            Assert.Equal("pc", tokens.Authenticate(token));
            Assert.Null(tokens.Authenticate(token + "bad"));
            tokens.Revoke("pc"); Assert.Null(tokens.Authenticate(token));
            var tokenEntry = store.Authority.ReadSnapshot().Get(AuthorityArea.Auth).GetProperty("tokens")[0];
            Assert.NotEqual(token, tokenEntry.GetProperty("hash").GetString());
            Assert.Empty(Directory.EnumerateFiles(store.Root, "*.db*"));
            Assert.True(tokenEntry.GetProperty("revoked").GetBoolean());
            Assert.Contains(store.Authority.ReadAudit(store.Authority.ReadSnapshot()),
                item => item.Action == "tokens-revoked");
        }
        finally { Directory.Delete(root, true); }
    }
}
