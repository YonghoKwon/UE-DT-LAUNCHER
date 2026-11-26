using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;
using Xunit;
using static UeDtLauncher.Tests.AuthorityStoreFixture;

namespace UeDtLauncher.Tests;

public sealed class CatalogAuthorityTests
{
    [Theory]
    [InlineData("GenerationWrite", false)]
    [InlineData("GenerationFlush", false)]
    [InlineData("GenerationRename", false)]
    [InlineData("GenerationDirectoryFlush", false)]
    [InlineData("HeadWrite", false)]
    [InlineData("HeadRename", true)]
    [InlineData("AuditAppend", true)]
    public async Task PublicationCrashReopensOldOrNewCatalogWithExactlyOneAudit(string boundary, bool published)
    {
        using var fixture = new CatalogFixture();
        var job = await fixture.Ingest("1.0.0", "first");
        var id = Selection("1.0.0").ReleaseId;
        using (var child = Start(fixture.AuthorityRoot, "catalog-approve", fixture.Config, job.Id, boundary))
        {
            var line = await child.ReadLine();
            while (line is not (null or "BOUNDARY")) line = await child.ReadLine();
            if (line is null) Assert.Fail((await child.Finish()).Error);
            child.Process.Kill();
            Assert.NotEqual(0, (await child.Finish()).ExitCode);
        }

        using (var fresh = Start(fixture.AuthorityRoot, "reopen"))
        {
            var reopened = await fresh.Finish();
            Assert.True(reopened.ExitCode == 0, reopened.Error);
            using var data = JsonDocument.Parse(reopened.Output);
            Assert.Equal(published ? 1 : 0, data.RootElement.GetProperty("generation").GetInt64());
            Assert.Equal(published ? 1 : 0, data.RootElement.GetProperty("areas").GetProperty("catalog")
                .GetProperty("releases").GetArrayLength());
            Assert.Equal(published ? 1 : 0, data.RootElement.GetProperty("events").GetArrayLength());
        }
        var final = Path.Combine(fixture.Intake.Root, "releases", id);
        var receipt = Path.Combine(final, "publication.json");
        Assert.True(File.Exists(receipt));
        var installedSignature = File.ReadAllBytes(Path.Combine(final, "manifest.json.sig"));
        Assert.Equal("publishing", fixture.Intake.Get(job.Id).State);
        var completed = await fixture.Publisher.ApproveAsync(job.Id);
        Assert.Equal(final.Replace('/', Path.DirectorySeparatorChar), completed.Directory);
        Assert.Equal(id, fixture.Publisher.FindByJob(job.Id)?.ReleaseId);
        Assert.Equal("published", fixture.Intake.Get(job.Id).State);
        Assert.Equal(installedSignature, File.ReadAllBytes(Path.Combine(final, "manifest.json.sig")));
        await fixture.Publisher.ApproveAsync(job.Id);
        var winner = Assert.Single(fixture.Audit("published"));
        Assert.Equal(job.Id, winner.Job);
        var losingJob = await fixture.Ingest("1.0.0", "second");
        var conflict = await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(losingJob.Id));
        Assert.Contains("belongs to another publication", conflict.Message);
        Assert.Equal(winner, Assert.Single(fixture.Audit("published")));
    }

    [Fact]
    public async Task UncommittedReleaseRejectsChangedPayloadAndKeepsOriginalOwnersReceipt()
    {
        using var fixture = new CatalogFixture();
        var job = await fixture.Ingest("1.0.0", "first");
        AuthorityDisk.Boundary.Value = point =>
        {
            if (point == AuthorityBoundary.BeforeHeadRename) throw new IOException("before catalog HEAD");
        };
        try { await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(job.Id)); }
        finally { AuthorityDisk.Boundary.Value = null; }
        var competitor = await fixture.Ingest("1.0.0", "second");
        var conflict = await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(competitor.Id));
        Assert.Contains("belongs to another publication", conflict.Message);
        var final = Path.Combine(fixture.Intake.Root, "releases", Selection("1.0.0").ReleaseId);
        await File.AppendAllTextAsync(Path.Combine(final, "files", "game.exe"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Publisher.ApproveAsync(job.Id));
        Assert.Null(fixture.Publisher.FindByJob(job.Id));
        Assert.Empty(fixture.Audit("published"));
    }

    [Fact]
    public async Task VerifiedMetadataCacheCannotMaskCorruptionOrBeMutatedByCallers()
    {
        using var fixture = new CatalogFixture();
        var release = await fixture.Publish("1.0.0");
        var loaded = fixture.Publisher.Find(release.ReleaseId)!;
        loaded.Metadata.DisplayName = "modified in caller";
        loaded.Metadata.ExecutablePaths.Add("added-by-caller");
        var other = fixture.Publisher.FindByJob(release.JobId)!;
        Assert.Equal("Demo", other.Metadata.DisplayName);
        Assert.Empty(other.Metadata.ExecutablePaths);
        var metadata = Path.Combine(fixture.Authority.ReleaseMetadataPath, release.ReleaseId + ".json");
        var captured = Path.Combine(fixture.Intake.Get(release.JobId).Snapshot!, "release.json");
        Assert.Equal(File.ReadAllBytes(captured), File.ReadAllBytes(metadata));
        File.AppendAllText(metadata, " ");
        Assert.Throws<InvalidDataException>(() => fixture.Publisher.Find(release.ReleaseId));
        Assert.Throws<InvalidDataException>(() => new ReleasePromotions(fixture.Intake).Snapshot());
    }

    [Fact]
    public async Task ProcessesCompeteForRevisionAndSameTargetDoesNotChangeHead()
    {
        using var fixture = new CatalogFixture();
        await fixture.Publish("1.0.0");
        await fixture.Publish("2.0.0");
        using var first = Start(fixture.AuthorityRoot, "catalog-promote", fixture.Config, "1.0.0", "0");
        using var second = Start(fixture.AuthorityRoot, "catalog-promote", fixture.Config, "2.0.0", "0");
        Assert.Equal("READY", await first.ReadLine());
        Assert.Equal("READY", await second.ReadLine());
        await first.Process.StandardInput.WriteLineAsync("START");
        await second.Process.StandardInput.WriteLineAsync("START");
        var exits = await Task.WhenAll(first.Finish(), second.Finish());
        Assert.Single(exits, result => result.ExitCode == 0);
        Assert.Contains("revision changed", Assert.Single(exits, result => result.ExitCode != 0).Error);
        var promotion = new ReleasePromotions(fixture.Intake);
        var winner = Assert.Single(promotion.Snapshot().Events);
        var version = winner.ReleaseId.Split('/')[3];
        var head = File.ReadAllBytes(fixture.Authority.DataPath("HEAD"));
        Assert.Equal(winner.Revision, promotion.Promote(Selection(version), winner.Revision, "repeat").Revision);
        Assert.Equal(head, File.ReadAllBytes(fixture.Authority.DataPath("HEAD")));
        Assert.Equal(winner.ReleaseId, Assert.Single(fixture.Audit("release-promoted")).Job);
    }

    [Fact]
    public async Task PromotionAuditStagingFailureDoesNotPublishCatalog()
    {
        using var fixture = new CatalogFixture();
        await fixture.Publish("1.0.0");
        var before = File.ReadAllBytes(fixture.Authority.DataPath("HEAD"));
        var writes = 0;
        AuthorityDisk.Boundary.Value = point =>
        {
            if (point == AuthorityBoundary.GenerationWrite && ++writes == 2) throw new IOException("audit write failed");
        };
        var promotions = new ReleasePromotions(fixture.Intake);
        try { Assert.Throws<IOException>(() => promotions.Promote(Selection("1.0.0"), 0, "proof")); }
        finally { AuthorityDisk.Boundary.Value = null; }
        Assert.Equal(before, File.ReadAllBytes(fixture.Authority.DataPath("HEAD")));
        Assert.Empty(promotions.Snapshot().Events);
        Assert.Empty(fixture.Audit("release-promoted"));
    }

    [Fact]
    public async Task SequenceReservesBeforeIssueSkipsFailedReservationsAndStopsAtOverflow()
    {
        using var fixture = new CatalogFixture();
        var fault = true;
        var allocator = new CatalogSequenceAllocator(fixture.Intake, 64, () =>
        {
            if (fault) throw new IOException("after reservation");
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => allocator.NextAsync(cancelled.Token));
        AuthorityDisk.Boundary.Value = point =>
        {
            if (point == AuthorityBoundary.BeforeHeadRename) throw new IOException("before reservation");
        };
        try { await Assert.ThrowsAsync<IOException>(() => allocator.NextAsync(default)); }
        finally { AuthorityDisk.Boundary.Value = null; }
        Assert.Equal(0, fixture.HighWater());
        await Assert.ThrowsAsync<IOException>(() => allocator.NextAsync(default));
        Assert.Equal(64, fixture.HighWater());
        fault = false;
        var values = await Task.WhenAll(Enumerable.Range(0, 300).Select(_ => Task.Run(() => allocator.NextAsync(default))));
        Assert.Equal(Enumerable.Range(65, 300).Select(n => (long)n), values.Order());
        Assert.Equal(384, fixture.HighWater());

        var reopened = new IntakeStore(fixture.Intake.Settings);
        CatalogAuthority.Bind(reopened, AuthorityStore.Open(fixture.AuthorityRoot));
        Assert.Equal(385, await new CatalogSequenceAllocator(reopened).NextAsync(default));
        fixture.Authority.Commit(_ => new(new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Sequence] = JsonSerializer.SerializeToElement(new { schemaVersion = 1, highWater = long.MaxValue })
        }, []));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CatalogSequenceAllocator(reopened).NextAsync(default));
        Assert.Equal(long.MaxValue, fixture.HighWater());
        fixture.Authority.Commit(_ => new(new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Sequence] = JsonSerializer.SerializeToElement(new { schemaVersion = 1, highWater = 1.5 })
        }, []));
        var corruptHead = File.ReadAllBytes(fixture.Authority.DataPath("HEAD"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CatalogSequenceAllocator(reopened).NextAsync(default));
        Assert.Equal(corruptHead, File.ReadAllBytes(fixture.Authority.DataPath("HEAD")));
        Assert.Empty(fixture.Authority.ReadAudit(fixture.Authority.ReadSnapshot()));
    }

    [Fact]
    public async Task ThousandHttpCatalogsStayConsistentAcrossPublicationAndPromotion()
    {
        using var fixture = new CatalogFixture();
        await fixture.Publish("1.0.0");
        var promotion = new ReleasePromotions(fixture.Intake);
        var first = promotion.Promote(Selection("1.0.0"), 0, "first");
        var token = new DistributionTokens(fixture.Intake).Issue("fixture");
        await using var app = DistributionHttp.CreateApplication(fixture.Intake);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var reachedWriter = new ManualResetEventSlim();
        using var allowWriter = new ManualResetEventSlim();
        var initial = await GetCatalog();
        Assert.Equal("1.0.0", Assert.Single(Assert.Single(initial.Projects).Releases).Version);
        var observations = new List<DistributionCatalog> { initial };
        var writer = Task.Run(async () =>
        {
            await fixture.Publish("2.0.0");
            ReleasePromotions.BeforeCommit.Value = () =>
            {
                reachedWriter.Set();
                if (!allowWriter.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Reader did not observe staged promotion.");
            };
            try { promotion.Promote(Selection("2.0.0"), first.Revision, "second"); }
            finally { ReleasePromotions.BeforeCommit.Value = null; }
        });
        try
        {
            Assert.True(reachedWriter.Wait(TimeSpan.FromSeconds(30)));
            for (var i = 0; i < 16; i++) observations.Add(await GetCatalog());
            Assert.All(observations.Skip(1), value => Assert.Equal("1.0.0", Recommendation(value)));
        }
        finally { allowWriter.Set(); }
        await writer;
        var toggles = Task.Run(() =>
        {
            var revision = promotion.Inspect(Selection("2.0.0")).Revision;
            for (var i = 0; i < 20; i++)
                revision = promotion.Promote(Selection(i % 2 == 0 ? "1.0.0" : "2.0.0"), revision, "overlap").Revision;
        });
        while (observations.Count < 999) observations.Add(await GetCatalog());
        await toggles;
        observations.Add(await GetCatalog());
        Assert.All(observations.Skip(1), value =>
        {
            Assert.Equal(new[] { "1.0.0", "2.0.0" }, Assert.Single(value.Projects).Releases.Select(r => r.Version));
            Assert.Contains(Recommendation(value), new[] { "1.0.0", "2.0.0" });
        });
        Assert.Equal("2.0.0", Recommendation(observations[^1]));
        Assert.Equal(1_000, observations.Select(value => value.Sequence).Distinct().Count());

        static string Recommendation(DistributionCatalog value) => Assert.Single(Assert.Single(value.Projects).Releases, r => r.IsLatest).Version;
        async Task<DistributionCatalog> GetCatalog()
        {
            var envelope = await http.GetFromJsonAsync<DistributionEnvelope>("/api/v1/catalog?selectionPolicy=explicit-promotion-v1");
            Assert.NotNull(envelope);
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = JsonSerializer.Deserialize<DetachedSignatureEnvelope>(envelope.SignatureDocument, JsonFiles.Options)!;
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(fixture.Intake.Settings.SigningKeyPath));
            Assert.True(key.VerifyData(payload, Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256));
            return JsonSerializer.Deserialize<DistributionCatalog>(payload, JsonFiles.Options)!;
        }
    }

    private static ReleaseSelection Selection(string version) => new("demo", "prod", "stable", "windows-x64", version);

    private sealed class CatalogFixture : IDisposable
    {
        private readonly AuthorityStoreFixture fixture = new();
        internal string AuthorityRoot { get; }
        internal string Config { get; }
        internal AuthorityStore Authority { get; }
        internal IntakeStore Intake { get; }
        internal ApprovedPublisher Publisher { get; }

        internal CatalogFixture()
        {
            AuthorityRoot = Path.Combine(fixture.Root, "files");
            Authority = AuthorityStore.Open(AuthorityRoot);
            var settings = new DistributionSettings
            {
                Root = Path.Combine(fixture.Root, "intake"),
                ListenUrl = "http://127.0.0.1:0",
                PolicyPath = Path.Combine(fixture.Root, "policy.json"),
                SigningKeyPath = Path.Combine(fixture.Root, "sign.pem")
            };
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(settings.SigningKeyPath, key.ExportECPrivateKeyPem());
            File.WriteAllText(settings.PolicyPath, """{"clients":[{"id":"fixture","addresses":["127.0.0.1/32"],"grants":[{"projectId":"demo","environment":"prod","channel":"stable"}]}]}""");
            Config = Path.Combine(fixture.Root, "settings.json");
            File.WriteAllText(Config, JsonSerializer.Serialize(settings, JsonFiles.Options));
            Intake = new(settings);
            CatalogAuthority.Bind(Intake, Authority);
            Publisher = new(Intake);
        }

        internal async Task<IntakeJob> Ingest(string version, string source)
        {
            var upload = Path.Combine(Intake.Root, "incoming", source);
            Directory.CreateDirectory(upload);
            var zip = Path.Combine(upload, "Windows.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var payload = new StreamWriter(archive.CreateEntry("game.exe").Open()))
                payload.Write("synthetic payload");
            var sidecarPath = Path.Combine(upload, "release.json");
            await SidecarPackageValidator.GenerateAsync(zip, sidecarPath,
                new() { ProjectId = "demo", DisplayName = "Demo", Version = version, EntryPoint = "game.exe" });
            using var sidecar = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecarPath));
            await File.WriteAllBytesAsync(sidecarPath, JsonSerializer.SerializeToUtf8Bytes(sidecar.RootElement));
            var job = await Intake.IngestAsync(upload);
            Assert.Equal("pending", job.State);
            return job;
        }

        internal async Task<PublishedRelease> Publish(string version) => await Publisher.ApproveAsync((await Ingest(version, version)).Id);
        internal StoredAuthorityEvent[] Audit(string action) => Authority.ReadAudit(Authority.ReadSnapshot()).Where(e => e.Action == action).ToArray();
        internal long HighWater() => Authority.ReadSnapshot().Get(AuthorityArea.Sequence).GetProperty("highWater").GetInt64();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            fixture.Dispose();
        }
    }
}
