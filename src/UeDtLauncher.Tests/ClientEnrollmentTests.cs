using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ClientEnrollmentTests
{
    [Theory]
    [InlineData("invalid-ip", "Invalid observed IPv4")]
    [InlineData("duplicate-key", "duplicate key ID")]
    [InlineData("different-pem", "different client or public key")]
    [InlineData("different-client", "different client or public key")]
    [InlineData("invalid-grant", "Grant 1")]
    [InlineData("private-pem", "invalid schema")]
    [InlineData("invalid-policy", "Access policy failed validation")]
    public async Task InvalidBatchKeepsStoreAndPolicy(string failure, string message)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var first = await fixture.AddBundleAsync("pc-a", "key-a");
        await fixture.AddBundleAsync("pc-b", failure == "duplicate-key" ? "key-a" : "key-b");
        await fixture.AddBundleAsync("pc-c", "key-c", invalidAddress: failure == "invalid-ip", privatePem: failure == "private-pem");
        if (failure == "different-pem")
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            fixture.Keys.Add(first.ClientId, new(1, first.KeyId, other.ExportSubjectPublicKeyInfoPem()));
        }
        if (failure == "different-client") fixture.Keys.Add("other-pc", new(1, first.KeyId, first.PublicKeyPem));
        if (failure == "invalid-policy") await File.WriteAllTextAsync(fixture.Settings.PolicyPath, "{\"clients\":null}");

        var before = fixture.Snapshot();
        var run = await fixture.RunAsync(grant: failure == "invalid-grant" ? "demo:prod" : "demo:prod:stable");
        Assert.Equal(1, run.Code);
        Assert.Contains(message, run.Error);
        Assert.DoesNotContain("PRIVATE KEY", run.Error + run.Output);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task DryRunAndReplayLeaveTreeUnchangedAndRetainExistingPolicy()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.AddBundleAsync("pc-a", "key-a");
        await fixture.AddBundleAsync("pc-b", "key-b");
        var oldPolicy = """
            {"note":"keep", "Clients":[{"id":"pc-a","addresses":["127.0.0.1"],"grants":[],"note":23}]}
            """;
        await File.WriteAllTextAsync(fixture.Settings.PolicyPath, oldPolicy);
        var before = fixture.Snapshot();
        var dry = await fixture.RunAsync(extra: ["--dry-run"]);
        Assert.Equal(0, dry.Code);
        Assert.Equal(before, fixture.Snapshot());

        var applied = await fixture.RunAsync();
        Assert.Equal(0, applied.Code);
        using var plan = JsonDocument.Parse(applied.Output);
        Assert.Equal("기존 항목 유지", plan.RootElement.GetProperty("clients")[0].GetProperty("policy").GetString());
        Assert.Equal(2, fixture.Keys.List().Count);
        using var merged = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Settings.PolicyPath));
        using var original = JsonDocument.Parse(oldPolicy);
        Assert.Equal("keep", merged.RootElement.GetProperty("note").GetString());
        var clients = merged.RootElement.GetProperty("Clients");
        Assert.Equal(JsonSerializer.Serialize(original.RootElement.GetProperty("Clients")[0]), JsonSerializer.Serialize(clients[0]));
        Assert.Equal("pc-b", clients[1].GetProperty("id").GetString());

        var after = fixture.Snapshot();
        var replay = await fixture.RunAsync();
        Assert.Equal(0, replay.Code);
        Assert.Equal(after, fixture.Snapshot());
    }

    [Fact]
    public async Task InterruptedAfterKeyRegistrationReplaysPolicyOnly()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.AddBundleAsync("pc-a", "key-a");
        await fixture.AddBundleAsync("pc-b", "key-b");
        await fixture.AddBundleAsync("pc-c", "key-c");
        var before = File.ReadAllBytes(fixture.Settings.PolicyPath);
        var run = await fixture.RunAsync(beforeReplace: () => throw new IOException("Test interruption."));
        Assert.Equal(1, run.Code);
        Assert.Equal(3, fixture.Keys.List().Count);
        Assert.Equal(before, File.ReadAllBytes(fixture.Settings.PolicyPath));
        var database = Fixture.ReadBytes(Path.Combine(fixture.Settings.Root, "distribution.db"));
        var recovery = await fixture.RunAsync();
        Assert.Equal(0, recovery.Code);
        using var result = JsonDocument.Parse(recovery.Output);
        Assert.Equal(0, result.RootElement.GetProperty("keysToAdd").GetInt32());
        Assert.Equal(3, result.RootElement.GetProperty("policiesToAdd").GetInt32());
        Assert.Equal(database, Fixture.ReadBytes(Path.Combine(fixture.Settings.Root, "distribution.db")));
        Assert.Equal(3, (await new FileAccessPolicyProvider(fixture.Settings.PolicyPath).LoadAsync(default)).Clients.Count);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp"));
    }

    [Fact]
    public async Task ReadOnlyValidationNeverLatchesAnExpiredRegistration()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var existing = await fixture.AddBundleAsync("pc-a", "key-a");
        fixture.Keys.Add(existing.ClientId, new(1, existing.KeyId, existing.PublicKeyPem), DateTimeOffset.UtcNow.AddDays(-1));
        var before = fixture.Snapshot();
        var dry = await fixture.RunAsync(extra: ["--dry-run"]);
        Assert.Equal(0, dry.Code);
        Assert.False(Assert.Single(fixture.Keys.List()).Expired);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStoreNeverInitializes(bool dryRun)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync(createStore: false);
        await fixture.AddBundleAsync("pc-a", "key-a");
        var before = fixture.Snapshot();
        var run = await fixture.RunAsync(extra: dryRun ? ["--dry-run"] : []);
        Assert.Equal(1, run.Code);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(fixture.Settings.Root));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-enroll-" + Guid.NewGuid().ToString("N"));
        internal DistributionSettings Settings { get; }
        internal DistributionDeviceKeys Keys { get; private set; } = null!;
        private string Config => Path.Combine(Root, "server.json");
        private string Bundles => Path.Combine(Root, "bundles");

        internal Fixture() => Settings = new() { Root = Path.Combine(Root, "store"), PolicyPath = Path.Combine(Root, "policy.json") };

        internal async Task InitializeAsync(bool createStore = true)
        {
            Directory.CreateDirectory(Bundles);
            await JsonFiles.WriteAsync(Config, Settings);
            await JsonFiles.WriteAsync(Settings.PolicyPath, new AccessPolicy());
            if (createStore) Keys = new(new IntakeStore(Settings));
        }

        internal async Task<DeviceEnrollmentBundle> AddBundleAsync(string client, string id, bool invalidAddress = false, bool privatePem = false)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var bundle = new DeviceEnrollmentBundle(1, client, id, privatePem ? key.ExportPkcs8PrivateKeyPem() : key.ExportSubjectPublicKeyInfoPem(),
                "test-host", [invalidAddress ? "invalid" : "10.1.2.3"], DateTimeOffset.UtcNow);
            await JsonFiles.WriteAsync(Path.Combine(Bundles, client + ".enroll.json"), bundle);
            return bundle;
        }

        internal async Task<(int Code, string Output, string Error)> RunAsync(string grant = "demo:prod:stable", string[]? extra = null, Action? beforeReplace = null)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await ClientEnrollment.RunAsync(["--from", Bundles, "--grant", grant, "--config", Config, .. extra ?? []], output, error, beforeReplace);
            return (code, output.ToString(), error.ToString());
        }

        internal string[] Snapshot() => Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Directory.Exists(path) ? "directory:" + path : path + ":" + Convert.ToHexString(SHA256.HashData(ReadBytes(path)))).ToArray();

        internal static byte[] ReadBytes(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }

        public void Dispose()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Settings.Root, "distribution.db") }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(Root, recursive: true);
        }
    }
}
