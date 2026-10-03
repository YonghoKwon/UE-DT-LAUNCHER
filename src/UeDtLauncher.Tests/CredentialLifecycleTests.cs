using Microsoft.Data.Sqlite;
using System.Net;
using System.Security.Cryptography;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class CredentialLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    public CredentialLifecycleTests() => store = new(new() { Root = root });
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    [Fact]
    public void ExpiryIsInclusiveDurableAndNeverRevivesAfterClockRollback()
    {
        var clock = new Clock(); var tokens = new DistributionTokens(store, clock);
        var token = tokens.Issue("pc", clock.Now.AddSeconds(1));
        Assert.Equal("pc", tokens.Authenticate(token)); clock.Now = clock.Now.AddSeconds(1);
        Assert.Null(tokens.Authenticate(token)); clock.Now = clock.Now.AddDays(-1);
        Assert.Null(new DistributionTokens(new(store.Settings), clock).Authenticate(token));
        Assert.True(tokens.List().Single().Expired);
    }
    [Fact]
    public void IndividualRevocationAndLegacyUnlimitedArePreserved()
    {
        var tokens = new DistributionTokens(store); var first = tokens.Issue("pc"); var second = tokens.Issue("pc");
        var id = tokens.List().First().Id; tokens.RevokeId(id);
        Assert.Null(tokens.Authenticate(first)); Assert.Equal("pc", tokens.Authenticate(second));
        Assert.All(tokens.List(), m => Assert.Null(m.ExpiresAtUtc));
        Assert.DoesNotContain(first, System.Text.Json.JsonSerializer.Serialize(tokens.List()));
    }
    [Fact]
    public void AuditFailureRollsBackIssueAndRevoke()
    {
        var tokens = new DistributionTokens(store); var token = tokens.Issue("pc"); var id = tokens.List().Single().Id;
        using var db = store.Open(); using var q = db.CreateCommand();
        q.CommandText = "CREATE TRIGGER deny_audit BEFORE INSERT ON audit BEGIN SELECT RAISE(ABORT,'injected'); END"; q.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => tokens.RevokeId(id)); Assert.Equal("pc", tokens.Authenticate(token));
        Assert.Throws<SqliteException>(() => tokens.Issue("pc")); Assert.Single(tokens.List());
    }
    [Fact]
    public void DeviceExpiryIsLatchedAndKeyIdCanNeverBeReused()
    {
        var clock = new Clock(); var keys = new DistributionDeviceKeys(store, clock);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registration = new DevicePublicKey(1, "pc-key", key.ExportSubjectPublicKeyInfoPem());
        keys.Add("pc", registration, clock.Now); Assert.True(keys.Find("pc-key")!.Expired);
        clock.Now = clock.Now.AddDays(-1); Assert.True(keys.Find("pc-key")!.Revoked);
        Assert.Throws<SqliteException>(() => keys.Add("another", registration));
    }
    [Fact]
    public void ExpiryAuditFailureNeverPartiallyCommitsAndConcurrentLatchIsOnce()
    {
        var clock=new Clock();var tokens=new DistributionTokens(store,clock);var token=tokens.Issue("pc",clock.Now);
        using(var db=store.Open()){using var q=db.CreateCommand();q.CommandText="CREATE TRIGGER fail_expiry BEFORE INSERT ON audit WHEN NEW.action='credential-expired' BEGIN SELECT RAISE(ABORT,'injected'); END";q.ExecuteNonQuery();}
        Assert.Throws<SqliteException>(()=>tokens.Authenticate(token));Assert.False(tokens.List().Single().Expired);
        using(var db=store.Open()){using var q=db.CreateCommand();q.CommandText="DROP TRIGGER fail_expiry";q.ExecuteNonQuery();}
        Parallel.For(0,10,_=>Assert.Null(tokens.Authenticate(token)));
        using var check=store.Open();using var count=check.CreateCommand();count.CommandText="SELECT COUNT(*) FROM audit WHERE action='credential-expired'";Assert.Equal(1L,count.ExecuteScalar());
    }
    [Fact]
    public void CanonicalAddressRejectsDuplicateMalformedProxyValuesAndNeverTrustsRemoteHeaders()
    {
        Assert.Null(DistributionClientAddress.Resolve(IPAddress.Loopback, ["1.2.3.4", "1.2.3.5"]));
        Assert.Null(DistributionClientAddress.Resolve(IPAddress.Loopback, ["bad"]));
        Assert.Equal(IPAddress.Parse("10.0.0.1"), DistributionClientAddress.Resolve(IPAddress.Parse("10.0.0.1"), ["1.2.3.4"]));
    }
    [Fact]
    public void RequestLimitsAreDisabledByDefaultAndNeverQueueExcessDownloads()
    {
        var unlimited = new DistributionRequestLimits(new()); Assert.True(unlimited.AllowRequest());
        var limited = new DistributionRequestLimits(new() { MaxApiRequestsPerSecond = 1, MaxConcurrentDownloads = 1 });
        Assert.True(limited.AllowRequest()); Assert.False(limited.AllowRequest());
        using (var first = limited.Download()) { Assert.NotNull(first); Assert.Null(limited.Download()); }
        using var next = limited.Download(); Assert.NotNull(next);
        Assert.Throws<InvalidDataException>(() => new DistributionRequestLimits(new() { MaxConcurrentDownloads = 0 }));
    }
}
