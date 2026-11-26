using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class CredentialAuthorityTests : IDisposable
{
    private readonly AuthorityStoreFixture fixture = new();
    private readonly string legacyRoot = Directory.CreateTempSubdirectory("uedt-auth-legacy-").FullName;
    private readonly AuthorityStore authority;
    private readonly IntakeStore intake;
    private readonly TestClock clock = new();
    private readonly DistributionTokens tokens;
    private readonly DistributionDeviceKeys keys;

    public CredentialAuthorityTests()
    {
        authority = fixture.Open();
        intake = new(new() { Root = legacyRoot });
        CredentialAuthority.Bind(intake, authority);
        tokens = new(intake, clock);
        keys = new(intake, clock);
    }

    [Theory]
    [InlineData("token-issue")]
    [InlineData("token-client-revoke")]
    [InlineData("token-id-revoke")]
    [InlineData("key-add")]
    [InlineData("key-revoke")]
    [InlineData("token-expiry")]
    [InlineData("key-expiry")]
    public void EventPreparationFailureLeavesHeadAndCredentialsUnchanged(string operation)
    {
        var token = tokens.Issue("pc", clock.Now.AddSeconds(1));
        var id = tokens.List().Single().Id;
        keys.Add("pc", PublicKey("existing"), clock.Now.AddSeconds(1));
        var candidate = PublicKey("candidate");
        var previous = authority.ReadSnapshot();
        var previousHead = HeadBytes();
        var previousAudit = authority.ReadAudit(previous).ToArray();
        Action mutate = operation switch
        {
            "token-issue" => () => tokens.Issue("another"),
            "token-client-revoke" => () => tokens.Revoke("pc"),
            "token-id-revoke" => () => tokens.RevokeId(id),
            "key-add" => () => keys.Add("pc", candidate),
            "key-revoke" => () => keys.Revoke("existing"),
            "token-expiry" => () => tokens.Authenticate(token),
            "key-expiry" => () => keys.Find("existing"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (operation.EndsWith("-expiry", StringComparison.Ordinal)) clock.Now = clock.Now.AddSeconds(1);

        // A credential mutation stages one auth document first, then the event batch.
        var writes = 0;
        AuthorityDisk.Boundary.Value = boundary =>
        {
            if (boundary == AuthorityBoundary.GenerationWrite && ++writes == 2)
                throw new IOException("Injected authority event preparation failure.");
        };
        try
        {
            Assert.Equal("Injected authority event preparation failure.", Assert.Throws<IOException>(mutate).Message);
        }
        finally { AuthorityDisk.Boundary.Value = null; }

        Assert.Equal(2, writes);
        Assert.Equal(previousHead, HeadBytes());
        var current = authority.ReadSnapshot();
        Assert.Equal(previous.Generation, current.Generation);
        Assert.Equal(previous.Get(AuthorityArea.Auth).GetRawText(), current.Get(AuthorityArea.Auth).GetRawText());
        Assert.Equal(previousAudit, authority.ReadAudit(current).ToArray());
        Assert.False(Assert.Single(tokens.List()).Expired);
        Assert.False(Assert.Single(keys.List()).Expired);
        clock.Now = clock.Now.AddDays(-1);
        Assert.Equal("pc", tokens.Authenticate(token));
        Assert.False(keys.Find("existing")!.Revoked);
        Assert.Null(keys.Find("candidate"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentExpiryPublishesExactlyOneLatchAndSurvivesReopen(bool deviceKey)
    {
        string id;
        string? token = null;
        if (deviceKey)
        {
            id = "due-key";
            keys.Add("pc", PublicKey(id), clock.Now);
        }
        else
        {
            token = tokens.Issue("pc", clock.Now);
            id = tokens.List().Single().Id;
        }
        var before = authority.ReadSnapshot().Generation;
        using var barrier = new Barrier(10);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Factory.StartNew(() =>
        {
            // All readers take the same pre-latch snapshot to race the writer.
            AuthorityStore.AfterReadHead.Value = generation =>
            {
                AuthorityStore.AfterReadHead.Value = null;
                Assert.Equal(before, generation);
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(15)));
            };
            if (deviceKey) Assert.True(keys.Find(id)!.Revoked);
            else Assert.Null(tokens.Authenticate(token));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        var current = authority.ReadSnapshot();
        Assert.Equal(before + 1, current.Generation);
        var audit = Assert.Single(authority.ReadAudit(current), entry => entry.Action == "credential-expired");
        Assert.Equal(id, audit.Job);
        clock.Now = clock.Now.AddDays(-1);
        var reopened = new IntakeStore(intake.Settings);
        CredentialAuthority.Bind(reopened, fixture.Open());
        if (deviceKey) Assert.True(new DistributionDeviceKeys(reopened, clock).Find(id)!.Expired);
        else Assert.Null(new DistributionTokens(reopened, clock).Authenticate(token));
        Assert.Equal(current.Generation, authority.ReadSnapshot().Generation);
    }

    [Fact]
    public void AddManyCommitsThreeKeysAndEventsOnceAndRejectsWholeBatchOnConflict()
    {
        var registrations = Batch();
        keys.AddMany(registrations);
        var first = authority.ReadSnapshot();
        Assert.Equal(1, first.Generation);
        Assert.Equal(registrations.Select(row => row.Key.KeyId), keys.List().Select(key => key.KeyId));
        var events = authority.ReadAudit(first).ToArray();
        Assert.Equal(3, events.Length);
        Assert.All(events, entry => Assert.Equal("device-key-added", entry.Action));
        Assert.Equal(registrations.Select(row => row.Key.KeyId), events.Select(entry => entry.Job));
        Assert.Equal([0, 1, 2], events.Select(entry => entry.Ordinal));
        Assert.Single(events.Select(entry => entry.TxId).Distinct());
        var head = HeadBytes();

        keys.AddMany(registrations);
        Assert.Equal(head, HeadBytes());
        Assert.Equal(events, authority.ReadAudit(authority.ReadSnapshot()).ToArray());

        var fresh = ("pc", PublicKey("fresh"), (DateTimeOffset?)null);
        Assert.Throws<InvalidDataException>(() => keys.AddMany([fresh, ("another", registrations[0].Key, null)]));
        Assert.Throws<InvalidDataException>(() => keys.AddMany([fresh, ("pc", PublicKey(registrations[0].Key.KeyId), null)]));
        Assert.Throws<InvalidDataException>(() => keys.AddMany([fresh, ("pc", PublicKey("bad") with { SchemaVersion = 2 }, null)]));
        Assert.Equal(head, HeadBytes());
        Assert.Equal(events, authority.ReadAudit(authority.ReadSnapshot()).ToArray());
        Assert.Null(keys.Find("fresh"));
    }

    [Fact]
    public void RepeatBatchCannotReactivateARevokedOrExpiredKeyOrChangeExpiry()
    {
        var revoked = PublicKey("revoked");
        var expired = PublicKey("expired");
        keys.AddMany([("pc", revoked, null), ("pc", expired, clock.Now)]);
        keys.Revoke(revoked.KeyId);
        Assert.True(keys.Find(expired.KeyId)!.Expired);
        var head = HeadBytes();

        keys.AddMany([("pc", revoked, clock.Now.AddDays(30)), ("pc", expired, null)]);

        Assert.Equal(head, HeadBytes());
        Assert.True(keys.Find(revoked.KeyId)!.Revoked);
        Assert.Null(keys.List().Single(key => key.KeyId == revoked.KeyId).ExpiresAtUtc);
        Assert.True(keys.Find(expired.KeyId)!.Expired);
        Assert.Throws<InvalidDataException>(() => keys.Add("pc", revoked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddManyInterruptedAtHeadPublicationRecoversOnlyAnEntireBatch(bool published)
    {
        var before = HeadBytes();
        var registrations = Batch();
        AuthorityDisk.Boundary.Value = boundary =>
        {
            if (boundary == (published ? AuthorityBoundary.HeadRename : AuthorityBoundary.BeforeHeadRename))
                throw new IOException("Injected batch publication interruption.");
        };
        try
        {
            if (published)
                Assert.Equal(1, Assert.Throws<AuthorityCommitDurabilityException>(() => keys.AddMany(registrations)).PublishedGeneration);
            else
                Assert.Throws<IOException>(() => keys.AddMany(registrations));
        }
        finally { AuthorityDisk.Boundary.Value = null; }

        var reopened = fixture.Open();
        var snapshot = reopened.ReadSnapshot();
        Assert.Equal(published ? 1 : 0, snapshot.Generation);
        Assert.Equal(published ? 3 : 0, snapshot.Get(AuthorityArea.Auth).GetProperty("deviceKeys").GetArrayLength());
        Assert.Equal(published ? 3 : 0, reopened.ReadAudit(snapshot).Count());
        if (!published) Assert.Equal(before, HeadBytes());
        var retry = new IntakeStore(intake.Settings);
        CredentialAuthority.Bind(retry, reopened);
        new DistributionDeviceKeys(retry).AddMany(registrations);
        Assert.Equal(1, reopened.ReadSnapshot().Generation);
        Assert.Equal(3, reopened.ReadAudit(reopened.ReadSnapshot()).Count());
    }

    [Fact]
    public void BoundStorageHoldsOnlyTokenHashAndPublicKeyAndDoesNotWriteLegacyCredentials()
    {
        var token = tokens.Issue("pc");
        var key = PublicKey("pc-key");
        keys.Add("pc", key);
        Assert.Equal("pc", tokens.Authenticate(token));
        var snapshot = authority.ReadSnapshot();
        var document = snapshot.Get(AuthorityArea.Auth);
        var serialized = document.GetRawText();
        Assert.DoesNotContain(token, serialized);
        Assert.DoesNotContain("PRIVATE KEY", serialized);
        Assert.Equal(key.PublicKeyPem, document.GetProperty("deviceKeys")[0].GetProperty("publicKeyPem").GetString());
        var hash = document.GetProperty("tokens")[0].GetProperty("hash").GetString();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))), hash);
        using var db = intake.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT (SELECT COUNT(*) FROM tokens) + (SELECT COUNT(*) FROM device_keys) + (SELECT COUNT(*) FROM audit)";
        Assert.Equal(0L, query.ExecuteScalar());
        Assert.Equal(AuthorityRootKind.LegacyDatabase, Assert.Throws<AuthorityRootException>(() => AuthorityStore.Open(legacyRoot)).Kind);
    }

    [Fact]
    public void RevocationAffectsOnlyTheRequestedIdentityAndSharesItsGenerationWithAudit()
    {
        var first = tokens.Issue("pc");
        var second = tokens.Issue("pc");
        var other = tokens.Issue("other");
        var id = tokens.List()[0].Id;
        keys.Add("pc", PublicKey("pc-key"));

        tokens.RevokeId(id);
        Assert.Null(tokens.Authenticate(first));
        Assert.Equal("pc", tokens.Authenticate(second));
        Assert.Equal("other", tokens.Authenticate(other));
        CheckLastEvent("token-revoked", id);

        tokens.Revoke("pc");
        Assert.Null(tokens.Authenticate(second));
        Assert.Equal("other", tokens.Authenticate(other));
        CheckLastEvent("tokens-revoked", "pc");

        keys.Revoke("pc-key");
        Assert.True(keys.Find("pc-key")!.Revoked);
        CheckLastEvent("device-key-revoked", "pc-key");

        void CheckLastEvent(string action, string identity)
        {
            var snapshot = authority.ReadSnapshot();
            var entry = Assert.Single(authority.ReadBatch(snapshot, snapshot.Generation).Events);
            Assert.Equal(snapshot.Generation, entry.Generation);
            Assert.Equal((action, identity), (entry.Action, entry.Job));
        }
    }

    [Fact]
    public void MissingOrDuplicateLatchFieldFailsClosed()
    {
        foreach (var fields in new[] { "", ",\"expired\":true,\"expired\":false" })
        {
            var json = "{\"schemaVersion\":1,\"tokens\":[{\"hash\":\"a\",\"id\":\"a\",\"client\":\"pc\"," +
                "\"revoked\":false,\"expiresAtUtc\":null" + fields + "}],\"deviceKeys\":[]}";
            using var damaged = JsonDocument.Parse(json);
            authority.Commit(_ => new(new Dictionary<AuthorityArea, JsonElement> { [AuthorityArea.Auth] = damaged.RootElement }, []));
            Assert.Throws<AuthorityStoreException>(() => tokens.Authenticate("fixture"));
        }
    }

    public void Dispose()
    {
        fixture.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(legacyRoot, recursive: true);
    }

    private byte[] HeadBytes() => File.ReadAllBytes(authority.DataPath("HEAD"));

    private static DevicePublicKey PublicKey(string id)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new(1, id, key.ExportSubjectPublicKeyInfoPem());
    }

    private static (string Client, DevicePublicKey Key, DateTimeOffset? Expiry)[] Batch() =>
        [("pc", PublicKey("key-a"), null), ("pc", PublicKey("key-b"), null), ("pc", PublicKey("key-c"), null)];

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
