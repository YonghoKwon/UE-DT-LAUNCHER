using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class OperationsConsoleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "console-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DistributionSettings settings;
    private readonly IntakeStore store;
    public OperationsConsoleTests()
    {
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "private.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        settings = new() { Root = Path.Combine(root, "server"), SigningKeyPath = keyPath, PolicyPath = Path.Combine(root, "policy.json") };
        File.WriteAllText(settings.PolicyPath, "{\"clients\":[]}");
        store = new(settings);
    }

    private async Task<IntakeJob> UploadAsync(string version)
    {
        var source = Path.Combine(store.Root, "incoming", "v" + version);
        Directory.CreateDirectory(source);
        var zip = Path.Combine(source, "payload.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("app.exe").Open())) writer.Write("synthetic only");
        await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(source, "release.json"), new()
        { ProjectId = "demo", Version = version, Platform = "windows-x64", EntryPoint = "app.exe" });
        return await store.IngestAsync(source);
    }

    private async Task<string> RunAsync(TextReader input, bool interactive = true)
    {
        using var output = new StringWriter();
        await new OperationsConsole(settings, Path.Combine(root, "server.json"), input, output, interactive).RunAsync();
        return output.ToString();
    }

    [Fact]
    public async Task ApprovalUsesPrivateSnapshotAndDoesNotPromote()
    {
        var job = await UploadAsync("1.0");
        var sourceMetadata = Path.Combine(job.Source, "release.json");
        File.WriteAllText(sourceMetadata, File.ReadAllText(sourceMetadata).Replace("1.0", "9.9"));
        var output = await RunAsync(new StringReader($"3\n{job.Id}\n{job.Id}\n0\n"));
        Assert.Equal("published", store.Get(job.Id).State);
        Assert.Contains("\"version\": \"1.0\"", output);
        Assert.DoesNotContain("9.9", output);
        Assert.Null(new ReleasePromotions(store).Inspect(new("demo", "prod", "stable", "windows-x64", "1.0")).RecommendedRelease);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    public async Task MismatchedConfirmationDoesNotChangeJob(string menu)
    {
        var job = await UploadAsync("1.0");
        await RunAsync(new StringReader($"{menu}\n{job.Id}\nwrong-id\n0\n"));
        Assert.Equal(job, store.Get(job.Id));
        Assert.Empty(new ApprovedPublisher(store).List());
    }

    [Fact]
    public async Task PromotionRechecksDisplayedRevision()
    {
        var one = await new ApprovedPublisher(store).ApproveAsync((await UploadAsync("1.0")).Id);
        var two = await new ApprovedPublisher(store).ApproveAsync((await UploadAsync("2.0")).Id);
        var selection = new ReleaseSelection("demo", "prod", "stable", "windows-x64", "2.0");
        using var input = new ActionReader(["6", one.ReleaseId, "validated", "1.0", "0"], index =>
        {
            if (index == 3) new ReleasePromotions(store).Promote(selection, 0, "concurrent operator");
        });
        await RunAsync(input);
        var state = new ReleasePromotions(store).Inspect(selection);
        Assert.Equal(two.ReleaseId, state.RecommendedRelease);
        Assert.Single(state.History);
    }

    [Fact]
    public async Task CleanupCandidatesExcludePublicPromotedPendingAndActive()
    {
        var published = await new ApprovedPublisher(store).ApproveAsync((await UploadAsync("1.0")).Id);
        new ReleasePromotions(store).Promote(new("demo", "prod", "stable", "windows-x64", "1.0"), 0, "validated");
        var pending = await UploadAsync("2.0");
        var failed = Path.Combine(store.Root, "incoming", "failed");
        Directory.CreateDirectory(failed); File.WriteAllText(Path.Combine(failed, "payload"), "failed");
        store.Save(new("failed", failed, "failed", null, null));
        var active = Path.Combine(store.Root, "processing", "active"); Directory.CreateDirectory(active);
        using (var db = store.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "INSERT INTO active_work VALUES('active','owner',$path,NULL,'now')";
            command.Parameters.AddWithValue("$path", active); command.ExecuteNonQuery();
        }
        var choices = await OperationsConsole.CleanupChoicesAsync(store);
        Assert.Contains(choices, c => c.JobId == "failed");
        Assert.DoesNotContain(choices, c => c.JobId == published.JobId || c.JobId == pending.Id || c.Temporary == "processing/active");
    }

    [Fact]
    public async Task NonterminalCannotMutateAndEofExitsSafely()
    {
        var job = await UploadAsync("1.0");
        await RunAsync(new StringReader($"3\n{job.Id}\n{job.Id}\n"), false);
        Assert.Equal("pending", store.Get(job.Id).State);
        await RunAsync(new StringReader(""));
        using var offline = DistributionMaintenanceLease.Acquire(store.Root, true);
    }

    [Fact]
    public async Task CredentialsShowMetadataOnly()
    {
        var secret = new DistributionTokens(store).Issue("pc");
        var output = await RunAsync(new StringReader("8\n0\n"));
        Assert.Contains("pc", output);
        Assert.DoesNotContain(secret, output);
        Assert.DoesNotContain("privatePem", output);
    }

    [Fact]
    public async Task SuccessfulPromotionUsesExplicitVersionConfirmation()
    {
        var release = await new ApprovedPublisher(store).ApproveAsync((await UploadAsync("1.0")).Id);
        await RunAsync(new StringReader($"6\n{release.ReleaseId}\nvalidated\n1.0\n0\n"));
        Assert.Equal(release.ReleaseId, new ReleasePromotions(store).Inspect(new("demo", "prod", "stable", "windows-x64", "1.0")).RecommendedRelease);
    }

    [Fact]
    public async Task FailedRetryUsesExistingStateTransition()
    {
        var source = Path.Combine(store.Root, "incoming", "failed"); Directory.CreateDirectory(source);
        store.Save(new("failed", source, "failed", null, "invalid archive"));
        await RunAsync(new StringReader("5\nfailed\nfailed\n0\n"));
        Assert.Equal("waiting", store.Get("failed").State);
    }

    [Theory]
    [InlineData("wrong\n0\n")]
    [InlineData("")]
    public async Task RetryPromotionAndCleanupRequireConfirmationEvenAtEof(string confirmation)
    {
        var source = Path.Combine(store.Root, "incoming", "failed");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload"), "synthetic only");
        store.Save(new("failed", source, "failed", null, null));
        await RunAsync(new StringReader("5\nfailed\n" + confirmation));
        Assert.Equal("failed", store.Get("failed").State);
        var release = await new ApprovedPublisher(store).ApproveAsync((await UploadAsync("1.0")).Id);
        await RunAsync(new StringReader($"6\n{release.ReleaseId}\nvalidated\n" + confirmation));
        Assert.Empty(new ReleasePromotions(store).Inspect(new("demo", "prod", "stable", "windows-x64", "1.0")).History);
        await RunAsync(new StringReader("7\njob:failed\n" + confirmation));
        Assert.True(File.Exists(Path.Combine(source, "payload")));
        Assert.Empty(Directory.GetFiles(store.Root, "console-retention-*.json"));
    }

    [Fact]
    public async Task ConfirmationPromptDoesNotHoldMaintenanceLease()
    {
        var job = await UploadAsync("1.0");
        var inspected = false;
        using var input = new ActionReader(["3", job.Id, "wrong", "0"], index =>
        {
            if (index != 2) return;
            using var exclusive = DistributionMaintenanceLease.Acquire(store.Root, true, create: false);
            inspected = true;
        });
        await RunAsync(input);
        Assert.True(inspected);
        Assert.Equal("pending", store.Get(job.Id).State);
    }

    [Fact]
    public async Task BusyCleanupPreservesPlanAndPrintsExactApplyCommand()
    {
        var source = Path.Combine(store.Root, "incoming", "failed"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload"), "failed");
        store.Save(new("failed", source, "failed", null, null));
        using var output = new StringWriter();
        using var input = new PlanReader(output);
        using (var live = DistributionMaintenanceLease.Acquire(store.Root, false))
            await new OperationsConsole(settings, Path.Combine(root, "server.json"), input, output, true).RunAsync();
        Assert.True(Directory.Exists(source));
        Assert.Single(Directory.GetFiles(store.Root, "console-retention-*.json"));
        Assert.Contains("retention apply --config", output.ToString());
        Assert.Contains("--confirm", output.ToString());
        using var exclusive = DistributionMaintenanceLease.Acquire(store.Root, true);
    }

    private sealed class PlanReader(StringWriter output) : TextReader
    {
        private int index;
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        {
            var value = index++ switch
            {
                0 => "7",
                1 => "job:failed",
                2 => System.Text.RegularExpressions.Regex.Match(output.ToString(), "\"planId\": \"([a-f0-9]+)\"").Groups[1].Value,
                _ => "0"
            };
            return ValueTask.FromResult<string?>(value);
        }
    }

    private sealed class ActionReader(string[] lines, Action<int> before) : TextReader
    {
        private int index;
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        {
            before(index);
            return ValueTask.FromResult(index < lines.Length ? lines[index++] : null);
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
