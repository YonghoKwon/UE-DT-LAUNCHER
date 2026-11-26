using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
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
        var before = fixture.Authority.ReadSnapshot().Generation;
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
            Assert.Equal(before + (published ? 2 : 1), data.RootElement.GetProperty("generation").GetInt64());
            Assert.Equal(published ? 1 : 0, data.RootElement.GetProperty("areas").GetProperty("catalog")
                .GetProperty("releases").GetArrayLength());
            var jobs = data.RootElement.GetProperty("areas").GetProperty("jobs");
            var persistedJob = Assert.Single(jobs.GetProperty("jobs").EnumerateArray());
            Assert.Equal(published ? "published" : "publishing", persistedJob.GetProperty("state").GetString());
            Assert.Equal(published ? 0 : 1, jobs.GetProperty("activeWork").GetArrayLength());
            Assert.Equal(published, persistedJob.GetProperty("claimId").ValueKind == JsonValueKind.Null);
            Assert.Equal(published, persistedJob.TryGetProperty("publishedAt", out _));
            Assert.Equal(published ? 1 : 0, data.RootElement.GetProperty("events").EnumerateArray()
                .Count(entry => entry.GetProperty("action").GetString() == "published"));
        }
        var final = Path.Combine(fixture.Intake.Root, "releases", id);
        var receipt = Path.Combine(final, "publication.json");
        Assert.True(File.Exists(receipt));
        var installedSignature = File.ReadAllBytes(Path.Combine(final, "manifest.json.sig"));
        Assert.Equal(published ? "published" : "publishing", fixture.Intake.Get(job.Id).State);
        var completed = await fixture.Publisher.ApproveAsync(job.Id);
        Assert.Equal(final.Replace('/', Path.DirectorySeparatorChar), completed.Directory);
        Assert.Equal(id, fixture.Publisher.FindByJob(job.Id)?.ReleaseId);
        var publishedJob = fixture.Intake.Get(job.Id);
        Assert.Equal("published", publishedJob.State);
        Assert.Equal("published", publishedJob.Phase);
        Assert.Equal(completed.Metadata.PackageSize, publishedJob.ProcessedBytes);
        Assert.Equal(completed.Metadata.PackageSize, publishedJob.TotalBytes);
        Assert.True(fixture.Intake.Authority.HasAudit(fixture.Intake.Authority.ReadSnapshot(), "published", job.Id));
        Assert.Equal(installedSignature, File.ReadAllBytes(Path.Combine(final, "manifest.json.sig")));
        await fixture.Publisher.ApproveAsync(job.Id);
        var winner = Assert.Single(fixture.Audit("published"));
        Assert.Equal(job.Id, winner.Job);
        var completedAt = winner.At.ToString("O");
        Assert.Equal(completedAt, publishedJob.ProgressUpdatedAt);
        var reopenedStore = new IntakeStore(fixture.Intake.Settings);
        Assert.Equal(completedAt, reopenedStore.Get(job.Id).ProgressUpdatedAt);
        Assert.Equal(completedAt, Assert.Single(reopenedStore.List()).ProgressUpdatedAt);
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
        CatalogAuthority.PublicationBoundary.Value = point =>
        {
            if (point == AuthorityBoundary.BeforeHeadRename) throw new IOException("before catalog HEAD");
        };
        try { await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(job.Id)); }
        finally { CatalogAuthority.PublicationBoundary.Value = null; }
        var competitor = await fixture.Ingest("1.0.0", "second");
        var conflict = await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(competitor.Id));
        Assert.Contains("belongs to another publication", conflict.Message);
        var final = Path.Combine(fixture.Intake.Root, "releases", Selection("1.0.0").ReleaseId);
        await File.AppendAllTextAsync(Path.Combine(final, "files", "game.exe"), "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Publisher.ApproveAsync(job.Id));
        Assert.Null(fixture.Publisher.FindByJob(job.Id));
        Assert.Empty(fixture.Audit("published"));
    }

    [Theory]
    [InlineData("files/unlisted.dat", true)]
    [InlineData("unexpected.json", true)]
    [InlineData("files/game.exe", false)]
    public async Task UncommittedReleaseRejectsDivergentInventory(string relative, bool added)
    {
        using var fixture = new CatalogFixture();
        var job = await fixture.Ingest("1.0.0", "first");
        CatalogAuthority.PublicationBoundary.Value = point =>
        {
            if (point == AuthorityBoundary.BeforeHeadRename) throw new IOException("before catalog HEAD");
        };
        try { await Assert.ThrowsAsync<IOException>(() => fixture.Publisher.ApproveAsync(job.Id)); }
        finally { CatalogAuthority.PublicationBoundary.Value = null; }

        var path = Path.Combine(fixture.Intake.Root, "releases", Selection("1.0.0").ReleaseId, relative);
        if (added) await File.WriteAllTextAsync(path, "unlisted fixture file");
        else File.Delete(path);
        var catalogBefore = fixture.CatalogIdentity();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Publisher.ApproveAsync(job.Id));
        Assert.Contains("inventory differs", error.Message);
        Assert.Equal(catalogBefore, fixture.CatalogIdentity());
        Assert.Null(fixture.Publisher.FindByJob(job.Id));
        Assert.Empty(fixture.Audit("published"));
    }

    [Fact]
    public async Task ExplicitNullDisplayNamePublishesAndRemainsReadable()
    {
        using var fixture = new CatalogFixture();
        var job = await fixture.Ingest("1.0.0", "unnamed", displayName: null);
        var release = await fixture.Publisher.ApproveAsync(job.Id);
        Assert.Null(release.Metadata.DisplayName);

        var reopened = new IntakeStore(fixture.Intake.Settings);
        var publisher = new ApprovedPublisher(reopened);
        Assert.Null(publisher.Find(release.ReleaseId)!.Metadata.DisplayName);
        Assert.Null(Assert.Single(publisher.List()).Metadata.DisplayName);

        var promotions = new ReleasePromotions(reopened);
        promotions.Promote(Selection("1.0.0"), 0, "fixture");
        var snapshot = promotions.Snapshot();
        var catalog = PromotionCatalog.Build(snapshot, snapshot.Releases, false, reopened.Settings.PublicUrl,
            new PublishedAssetCache(), 1, DateTimeOffset.UtcNow);
        Assert.Null(Assert.Single(catalog.Projects).DisplayName);
    }

    [Fact]
    public async Task PublicationTimeSurvivesReopenOfJobsWrittenBeforeTimestampField()
    {
        using var fixture = new CatalogFixture();
        var release = await fixture.Publish("1.0.0");
        var completedAt = Assert.Single(fixture.Audit("published")).At.ToString("O");
        fixture.Authority.Commit(snapshot =>
        {
            var jobs = IntakeJobs.Read(snapshot);
            var job = Assert.Single(jobs.Jobs);
            Assert.NotNull(job.PublishedAt);
            jobs.Jobs[0] = job with { PublishedAt = null };
            return jobs.Plan();
        });

        var reopened = new IntakeStore(fixture.Intake.Settings);
        Assert.Equal(completedAt, reopened.Get(release.JobId).ProgressUpdatedAt);
        Assert.Equal(completedAt, Assert.Single(reopened.List()).ProgressUpdatedAt);
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
        var promotions = new ReleasePromotions(fixture.Intake);
        Assert.Equal("Demo", Assert.Single(promotions.Snapshot().Releases).Metadata.DisplayName);
        var modified = File.GetLastWriteTimeUtc(metadata);
        var originalLength = new FileInfo(metadata).Length;
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("Demo", "Demi", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(metadata, modified);
        Assert.Equal(originalLength, new FileInfo(metadata).Length);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(metadata));
        Assert.Throws<InvalidDataException>(() => fixture.Publisher.Find(release.ReleaseId));
        Assert.Throws<InvalidDataException>(() => promotions.Snapshot());
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
        var token = new DistributionTokens(fixture.Intake).Issue("fixture");
        Action<long>? pauseReader = null;
        await using var app = DistributionHttp.CreateApplicationCore(fixture.Intake, new()
        {
            AfterCatalogSnapshotRead = generation => Interlocked.Exchange(ref pauseReader, null)?.Invoke(generation)
        });
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var initial = await GetCatalog();
        AssertState(initial, ["1.0.0"], latest: null);
        var observations = new List<DistributionCatalog> { initial };

        var beforePublish = await ReadAcross(async () =>
        {
            await fixture.Publish("2.0.0");
            promotion.Promote(Selection("1.0.0"), 0, "first");
        });
        // A recommendation for 1.0.0 without 2.0.0 in the inventory never existed.
        AssertState(beforePublish, ["1.0.0"], latest: null);
        observations.Add(beforePublish);
        var afterPublish = await GetCatalog();
        AssertState(afterPublish, ["1.0.0", "2.0.0"], latest: "1.0.0");
        observations.Add(afterPublish);

        var beforePromotion = await ReadAcross(() =>
        {
            promotion.Promote(Selection("2.0.0"), 1, "second");
            return Task.CompletedTask;
        });
        AssertState(beforePromotion, ["1.0.0", "2.0.0"], latest: "1.0.0");
        observations.Add(beforePromotion);

        var toggles = Task.Run(() =>
        {
            var revision = promotion.Inspect(Selection("2.0.0")).Revision;
            for (var i = 0; i < 20; i++)
                revision = promotion.Promote(Selection(i % 2 == 0 ? "1.0.0" : "2.0.0"), revision, "overlap").Revision;
        });
        while (observations.Count < 999) observations.Add(await GetCatalog());
        await toggles;
        observations.Add(await ReadAcross(() =>
        {
            new DistributionTokens(fixture.Intake).Revoke("fixture");
            return Task.CompletedTask;
        }));
        using (var revoked = await http.GetAsync("/api/v1/catalog?selectionPolicy=explicit-promotion-v1"))
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.All(observations.Skip(4), value =>
        {
            Assert.Equal(new[] { "1.0.0", "2.0.0" }, Assert.Single(value.Projects).Releases.Select(r => r.Version));
            Assert.Contains(Recommendation(value), new[] { "1.0.0", "2.0.0" });
        });
        AssertState(observations[^1], ["1.0.0", "2.0.0"], latest: "2.0.0");
        Assert.Equal(1_000, observations.Select(value => value.Sequence).Distinct().Count());

        async Task<DistributionCatalog> ReadAcross(Func<Task> write)
        {
            var expectedGeneration = fixture.Authority.ReadSnapshot().Generation;
            var reached = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resume = new ManualResetEventSlim();
            Interlocked.Exchange(ref pauseReader, generation =>
            {
                reached.SetResult(generation);
                if (!resume.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("HEAD reader was not resumed.");
            });
            var request = GetCatalog();
            try
            {
                Assert.Equal(expectedGeneration, await reached.Task.WaitAsync(TimeSpan.FromSeconds(30)));
                await write();
                Assert.True(fixture.Authority.ReadSnapshot().Generation > expectedGeneration);
            }
            finally
            {
                Interlocked.Exchange(ref pauseReader, null);
                resume.Set();
                await request;
            }
            return await request;
        }

        static void AssertState(DistributionCatalog value, string[] versions, string? latest)
        {
            var releases = Assert.Single(value.Projects).Releases;
            Assert.Equal(versions, releases.Select(release => release.Version));
            Assert.Equal(latest is null ? Array.Empty<string>() : [latest], releases.Where(release => release.IsLatest).Select(release => release.Version));
        }

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
            var settings = new DistributionSettings
            {
                Root = AuthorityRoot,
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
            Authority = Intake.Authority;
            Publisher = new(Intake);
        }

        internal async Task<IntakeJob> Ingest(string version, string source, string? displayName = "Demo")
        {
            var upload = Path.Combine(Intake.Root, "incoming", source);
            Directory.CreateDirectory(upload);
            var zip = Path.Combine(upload, "Windows.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var payload = new StreamWriter(archive.CreateEntry("game.exe").Open()))
                payload.Write("synthetic payload");
            var sidecarPath = Path.Combine(upload, "release.json");
            await SidecarPackageValidator.GenerateAsync(zip, sidecarPath,
                new() { ProjectId = "demo", DisplayName = displayName!, Version = version, EntryPoint = "game.exe" });
            using var sidecar = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecarPath));
            await File.WriteAllBytesAsync(sidecarPath, JsonSerializer.SerializeToUtf8Bytes(sidecar.RootElement));
            var job = await Intake.IngestAsync(upload);
            Assert.Equal("pending", job.State);
            return job;
        }

        internal async Task<PublishedRelease> Publish(string version) => await Publisher.ApproveAsync((await Ingest(version, version)).Id);
        internal StoredAuthorityEvent[] Audit(string action) => Authority.ReadAudit(Authority.ReadSnapshot()).Where(e => e.Action == action).ToArray();
        internal (string Path, string Sha256) CatalogIdentity()
        {
            var head = Authority.ReadSnapshot().Head;
            return (head.Files["catalog"], head.Sha256["catalog"]);
        }

        internal long HighWater() => Authority.ReadSnapshot().Get(AuthorityArea.Sequence).GetProperty("highWater").GetInt64();

        public void Dispose()
        {
            fixture.Dispose();
        }
    }
}
