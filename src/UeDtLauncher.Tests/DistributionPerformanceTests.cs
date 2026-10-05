using System.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class DistributionPerformanceTests
{
    [Fact]
    public async Task PolicyReusesOnlyIdenticalBytesAndFailsClosedAfterEdits()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "policy.json");
        await JsonFiles.WriteAsync(path, Policy("1.0.0"));
        var provider = new FileAccessPolicyProvider(path);
        var first = await provider.LoadCompiledAsync(default);
        Assert.Same(first, await provider.LoadCompiledAsync(default));
        var modified = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        await JsonFiles.WriteAsync(path, Policy("2.0.0"));
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Equal(length, new FileInfo(path).Length);
        var second = await provider.LoadCompiledAsync(default);
        Assert.NotSame(first, second);
        Assert.False(second.FindClient("a")!.AllowsRelease(Metadata("1.0.0")));
        Assert.True(second.FindClient("a")!.AllowsRelease(Metadata("2.0.0")));
        Assert.True(second.FindClient("a")!.AllowsAddress(IPAddress.Parse("::ffff:127.0.0.1")));
        await File.WriteAllTextAsync(path, "{");
        await Assert.ThrowsAsync<JsonException>(() => provider.LoadCompiledAsync(default));
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.LoadCompiledAsync(default));
        await File.WriteAllTextAsync(path, new string(' ', FileAccessPolicyProvider.MaxPolicyBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadCompiledAsync(default));
        await JsonFiles.WriteAsync(path, Policy("1.0.0"));
        Assert.True((await provider.LoadCompiledAsync(default)).FindClient("a")!.AllowsRelease(Metadata("1.0.0")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"clients\":null}")]
    [InlineData("{\"clients\":[null]}")]
    [InlineData("{\"clients\":[{\"id\":\"a\",\"addresses\":null}]}")]
    [InlineData("{\"clients\":[{\"id\":\"a\",\"grants\":[null]}]}")]
    [InlineData("{\"clients\":[{\"id\":\"a\"},{\"id\":\"a\"}]}")]
    [InlineData("{\"clients\":[{\"id\":\"a\",\"grants\":[{\"projectId\":\"demo\",\"environment\":\"invalid\",\"channel\":\"stable\"}]}]}")]
    public async Task NullAndDuplicatePolicyStructuresAreInvalid(string json)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "policy.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileAccessPolicyProvider(path).LoadCompiledAsync(default));
    }

    [Fact]
    public void CompiledSnapshotDoesNotExposeMutablePolicyCollections()
    {
        var policy = Policy("1.0.0");
        var compiled = CompiledAccessPolicy.Create(policy);
        policy.Clients[0].Addresses.Clear();
        policy.Clients[0].Grants[0].Versions[0] = "2.0.0";
        policy.Clients.Clear();
        Assert.True(compiled.FindClient("a")!.AllowsAddress(IPAddress.Loopback));
        Assert.True(compiled.FindClient("a")!.AllowsRelease(Metadata("1.0.0")));
        Assert.False(compiled.FindClient("a")!.AllowsRelease(Metadata("2.0.0")));
    }

    [Fact]
    public async Task AssetCacheIsImmutableBoundedLruAndPublicationScoped()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = Path.Combine(directory.Path, "manifest.json");
        await JsonFiles.WriteAsync(manifestPath, Manifest("hero-first"));
        var release = new PublishedRelease(Metadata("1.0.0").ReleaseId, "job-1", directory.Path, Metadata("1.0.0"));
        var cache = new PublishedAssetCache(maxEntries: 2, maxBytes: 4096);
        var first = cache.Get(release);
        Assert.Same(first, cache.Get(release));
        Assert.Equal("hero-first", first.Hero!.Sha256);
        Assert.Equal("thumbnail-first", first.Thumbnail!.Sha256);
        var other = release with { JobId = "job-2" };
        _ = cache.Get(other);
        _ = cache.Get(release); // Touch the oldest entry; it must survive the next eviction.
        _ = cache.Get(release with { JobId = "job-3" });
        Assert.Equal(2, cache.Count);
        Assert.Same(first, cache.Get(release));
        Assert.InRange(cache.RetainedBytes, 1, 4096);
        await JsonFiles.WriteAsync(manifestPath, Manifest("hero-second"));
        Assert.Equal("hero-second", cache.Get(other).Hero!.Sha256); // Evicted job is read again.
        Assert.Equal("hero-first", first.Hero.Sha256);
        var tooSmall = new PublishedAssetCache(maxEntries: 2, maxBytes: 64);
        _ = tooSmall.Get(release);
        Assert.Equal(0, tooSmall.Count);
        Assert.Equal(0, tooSmall.RetainedBytes);
        var byteLimited = new PublishedAssetCache(maxEntries: 256, maxBytes: 1024);
        for (var i = 0; i < 20; i++) _ = byteLimited.Get(release with { JobId = "job-" + i });
        Assert.InRange(byteLimited.RetainedBytes, 0, 1024);
        Assert.True(byteLimited.Count < 20);
    }

    [Theory]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64/manifest.json", true)]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64/files/sub/file.bin", true)]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64", false)]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64/", false)]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64-extra/manifest.json", true)]
    [InlineData("/releases/demo/prod/stable/1.0.0/windows-x64%2Ffiles/manifest.json", false)]
    [InlineData("/releases/demo/prod/stable/1.0.0' OR 1=1/windows-x64/manifest.json", false)]
    [InlineData("/Releases/demo/prod/stable/1.0.0/windows-x64/manifest.json", false)]
    public void ReleaseLookupExtractsExactlyFiveSafeSegments(string path, bool valid)
    {
        Assert.Equal(valid, DistributionHttp.TryGetReleaseId(path, out var id));
        if (valid) Assert.Equal(string.Join('/', path.Split('/').Skip(2).Take(5)), id);
    }

    [Fact]
    public async Task HttpCatalogIsFreshClientScopedAndKeepsApprovalOrder()
    {
        await using var server = await ServerFixture.CreateAsync();
        using var a = await server.GetAsync("/api/v1/catalog", "a");
        using var a2 = await server.GetAsync("/api/v1/catalog", "a");
        using var b = await server.GetAsync("/api/v1/catalog", "b");
        using var all = await server.GetAsync("/api/v1/catalog", "all");
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.True(a.Headers.CacheControl!.NoStore);
        var (catalogA, envelopeA) = await ReadCatalog(a);
        var (catalogA2, envelopeA2) = await ReadCatalog(a2);
        var (catalogB, _) = await ReadCatalog(b);
        var (catalogAll, _) = await ReadCatalog(all);
        Assert.True(catalogA2.Sequence > catalogA.Sequence);
        Assert.NotEqual(envelopeA.Payload, envelopeA2.Payload);
        Assert.NotEqual(envelopeA.SignatureDocument, envelopeA2.SignatureDocument);
        Assert.True(server.VerifySignature(envelopeA));
        Assert.True(server.VerifySignature(envelopeA2));
        Assert.True(DateTimeOffset.Parse(catalogA2.IssuedAtUtc!) >= DateTimeOffset.Parse(catalogA.IssuedAtUtc!));
        Assert.Equal("1.0.0", Assert.Single(Assert.Single(catalogA.Projects).Releases).Version);
        Assert.Equal("2.0.0", Assert.Single(Assert.Single(catalogB.Projects).Releases).Version);
        Assert.Contains("/1.0.0/", catalogA.Projects[0].Hero!.Url);
        Assert.Contains("/2.0.0/", catalogB.Projects[0].Hero!.Url);
        var versions = Assert.Single(catalogAll.Projects).Releases;
        Assert.Equal(new[] { "2.0.0", "1.0.0" }, versions.Select(r => r.Version));
        Assert.Equal("1.0.0", Assert.Single(versions, r => r.IsLatest).Version);
        Assert.Equal(10, (DateTimeOffset.Parse(catalogA.ExpiresAtUtc!) - DateTimeOffset.Parse(catalogA.IssuedAtUtc!)).TotalMinutes);
        using var head = await server.GetAsync("/api/v1/catalog", "a", HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.True(head.Headers.CacheControl!.NoStore);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            var client = i % 2 == 0 ? "a" : "b";
            using var response = await server.GetAsync("/api/v1/catalog", client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var (catalog, envelope) = await ReadCatalog(response);
            Assert.Equal(client == "a" ? "1.0.0" : "2.0.0", Assert.Single(Assert.Single(catalog.Projects).Releases).Version);
            Assert.True(server.VerifySignature(envelope));
            return catalog.Sequence;
        }));
        Assert.Equal(parallel.Length, parallel.Distinct().Count());
        Assert.All(parallel, sequence => Assert.True(sequence > catalogAll.Sequence));
    }

    [Fact]
    public async Task HttpMixedReadersAndSequenceWritersRemainCorrectConcurrently()
    {
        await using var server = await ServerFixture.CreateAsync();
        var sequences = await Task.WhenAll(Enumerable.Range(0, 30).Select(async worker =>
        {
            var client = worker % 2 == 0 ? "a" : "b";
            var version = client == "a" ? "1.0.0" : "2.0.0";
            var path = $"/releases/demo/prod/stable/{version}/windows-x64/";
            var issued = new List<long>();
            for (var index = 0; index < 12; index++)
            {
                if (index % 3 == 0)
                {
                    using var response = await server.GetAsync("/api/v1/catalog", client);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var (catalog, _) = await ReadCatalog(response);
                    Assert.Equal(version, Assert.Single(Assert.Single(catalog.Projects).Releases).Version);
                    issued.Add(catalog.Sequence);
                }
                else if (index % 3 == 1)
                {
                    using var response = await server.GetAsync(path + "manifest.json", client, HttpMethod.Head);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                }
                else
                {
                    using var response = await server.GetAsync(path + "files/game.bin", client, range: "bytes=2-4");
                    Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
                    Assert.Equal("cde", await response.Content.ReadAsStringAsync());
                }
            }
            return issued;
        }));
        var all = sequences.SelectMany(values => values).ToList();
        Assert.Equal(120, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task HttpCatalogAndBackgroundClaimsProgressAndPublicationShareSqlCoordination()
    {
        await using var server = await ServerFixture.CreateAsync();
        // Separate store objects for the same root must participate in the same process-local gate.
        var stores = new[] { new IntakeStore(server.Store.Settings), new IntakeStore(server.Store.Settings) };
        var uploads = new List<string>();
        for (var index = 0; index < 8; index++)
        {
            var upload = Path.Combine(server.Store.Root, "incoming", "concurrent-" + index);
            Directory.CreateDirectory(upload);
            var zip = Path.Combine(upload, "game.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("game.bin").Open())) writer.Write(new string('x', 128 * 1024));
            await SidecarPackageValidator.GenerateAsync(zip, Path.Combine(upload, "release.json"),
                new() { ProjectId = "demo", Version = "3.0." + index, EntryPoint = "game.bin" });
            uploads.Add(upload);
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerProbe = 0;
        var progressCount = 0;
        var writes = stores.Select((store, worker) => Task.Run(async () =>
        {
            await start.Task;
            for (var index = worker; index < uploads.Count; index += stores.Length)
            {
                var job = await store.IngestAsync(uploads[index], observer: new InlinePackageProgress(_ =>
                {
                    Interlocked.Increment(ref progressCount);
                    if (Interlocked.CompareExchange(ref observerProbe, 1, 0) == 0)
                    {
                        // Deadlock guard, not a throughput assertion: observers must run outside the SQL gate.
                        var nestedWrite = Task.Run(() => server.Store.Audit("observer-probe", "synthetic"));
                        Assert.True(nestedWrite.Wait(TimeSpan.FromSeconds(15)), "Progress observer retained the database gate.");
                        nestedWrite.GetAwaiter().GetResult();
                    }
                }));
                Assert.Equal("pending", job.State);
                await new ApprovedPublisher(store).ApproveAsync(job.Id);
                Assert.Equal("published", store.Get(job.Id).State);
            }
        })).ToArray();
        var reads = Enumerable.Range(0, 24).Select(async _ =>
        {
            await start.Task;
            var sequences = new List<long>();
            for (var index = 0; index < 6; index++)
            {
                using var response = await server.GetAsync("/api/v1/catalog", "a");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var (catalog, envelope) = await ReadCatalog(response);
                Assert.True(server.VerifySignature(envelope));
                Assert.Equal("1.0.0", Assert.Single(Assert.Single(catalog.Projects).Releases).Version);
                sequences.Add(catalog.Sequence);
                using var denied = await server.GetAsync("/releases/demo/prod/stable/9.0.0/windows-x64/manifest.json", "a");
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); // Nested HTTP writer -> Audit writer.
            }
            return sequences;
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(writes.Cast<Task>().Concat(reads));
        var sequences = reads.SelectMany(task => task.Result).ToList();
        Assert.Equal(144, sequences.Count);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
        Assert.True(progressCount >= 8 * 4);
        Assert.Equal(8, server.Store.List().Count);
        Assert.All(server.Store.List(), job => Assert.Equal("published", job.State));
        Assert.Equal(10, new ApprovedPublisher(server.Store).List().Count);
        using var db = server.Store.Open(); using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM active_work";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    private sealed class InlinePackageProgress(Action<PackageWorkProgress> callback) : IProgress<PackageWorkProgress>
    {
        public void Report(PackageWorkProgress value) => callback(value);
    }

    [Fact]
    public async Task CancelledCatalogRequestsDoNotBlockLaterReadsAndRevocation()
    {
        await using var server = await ServerFixture.CreateAsync();
        using (var warmup = await server.GetAsync("/api/v1/catalog", "a")) Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        using var cancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 24).Select(async _ =>
        {
            try
            {
                using var response = await server.GetAsync("/api/v1/catalog", "a", token: cancellation.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }).ToArray();
        cancellation.Cancel();
        await Task.WhenAll(pending);
        var prefix = "/releases/demo/prod/stable/1.0.0/windows-x64/";
        using var catalog = await server.GetAsync("/api/v1/catalog", "a");
        using var head = await server.GetAsync(prefix + "manifest.json", "a", HttpMethod.Head);
        using var range = await server.GetAsync(prefix + "files/game.bin", "a", range: "bytes=2-4");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        new DistributionTokens(server.Store).Revoke("a");
        using var revoked = await server.GetAsync("/api/v1/catalog", "a");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task HttpFileHeadRangeInternalAuthorizationEditsAndRevokeAreFresh()
    {
        await using var server = await ServerFixture.CreateAsync();
        var prefix = "/releases/demo/prod/stable/1.0.0/windows-x64/";
        using var head = await server.GetAsync(prefix + "files/game.bin", "a", HttpMethod.Head);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(10, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var range = await server.GetAsync(prefix + "files/game.bin", "a", range: "bytes=2-4");
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal("cde", await range.Content.ReadAsStringAsync());
        using var internalOk = await server.GetAsync("/internal/authorize", "a", original: prefix + "files/game.bin?ignored=yes");
        Assert.Equal(HttpStatusCode.NoContent, internalOk.StatusCode);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var denied = await server.GetAsync(prefix + "files/game.bin", "b", method);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var deniedRange = await server.GetAsync(prefix + "files/game.bin", "b", range: "bytes=2-4");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRange.StatusCode);
        using var internalDenied = await server.GetAsync("/internal/authorize", "b", original: prefix + "files/game.bin");
        Assert.Equal(HttpStatusCode.Forbidden, internalDenied.StatusCode);
        using var prefixCollision = await server.GetAsync(prefix.Replace("windows-x64/", "windows-x64-extra/") + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.Forbidden, prefixCollision.StatusCode);
        using var unknown = await server.GetAsync(prefix.Replace("1.0.0", "1.0.0-extra") + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        using var noQueryTokens = await server.GetAsync(prefix + "manifest.json?token=" + server.Tokens["a"], null);
        Assert.Equal(HttpStatusCode.Unauthorized, noQueryTokens.StatusCode);
        using var wrongIp = await server.GetAsync(prefix + "manifest.json", "a", ip: "10.0.0.1");
        Assert.Equal(HttpStatusCode.Forbidden, wrongIp.StatusCode);

        // A corrupt unrelated DB row would fail a full List(); file paths must use exact parameterized lookup.
        using (var db = server.Store.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "INSERT INTO releases VALUES('unrelated','unused','unused','{')";
            command.ExecuteNonQuery();
        }
        using var exact = await server.GetAsync(prefix + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        var modified = File.GetLastWriteTimeUtc(server.Store.Settings.PolicyPath);
        await JsonFiles.WriteAsync(server.Store.Settings.PolicyPath, Policy("2.0.0"));
        File.SetLastWriteTimeUtc(server.Store.Settings.PolicyPath, modified);
        using var afterEdit = await server.GetAsync(prefix + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.Forbidden, afterEdit.StatusCode);
        using var afterEditAllowed = await server.GetAsync(prefix.Replace("1.0.0", "2.0.0") + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.OK, afterEditAllowed.StatusCode);
        await File.WriteAllTextAsync(server.Store.Settings.PolicyPath, "{");
        using var malformed = await server.GetAsync(prefix + "manifest.json", "a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, malformed.StatusCode);
        File.Delete(server.Store.Settings.PolicyPath);
        using var deleted = await server.GetAsync("/internal/authorize", "a", original: prefix + "manifest.json");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, deleted.StatusCode);
        await JsonFiles.WriteAsync(server.Store.Settings.PolicyPath, Policy("1.0.0"));
        new DistributionTokens(server.Store).Revoke("a");
        using var revoked = await server.GetAsync(prefix + "manifest.json", "a");
        using var revokedInternal = await server.GetAsync("/internal/authorize", "a", original: prefix + "manifest.json");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedInternal.StatusCode);
    }

    [Theory]
    [InlineData("GET","files/game.bin",null,200)]
    [InlineData("HEAD","files/game.bin",null,200)]
    [InlineData("GET","files/hero.png",null,200)]
    [InlineData("HEAD","manifest.json.sig",null,200)]
    [InlineData("GET","files/missing.bin",null,404)]
    [InlineData("GET","files/game.bin","bytes=100-",416)]
    [InlineData("GET","files/game.bin","bytes=2-4",206)]
    public async Task LimitedBearerErrorsHeadRangeAndRevocationReturnSlots(string method,string suffix,string? range,int status)
    {
        await using var s=await ServerFixture.CreateAsync(c=>c.MaxConcurrentDownloads=1);
        await File.WriteAllTextAsync(Path.Combine(s.Store.Root,"releases/demo/prod/stable/1.0.0/windows-x64/files/hero.png"),"image");
        var prefix="/releases/demo/prod/stable/1.0.0/windows-x64/";
        using(var response=await s.GetAsync(prefix+suffix,"a",new(method),range))Assert.Equal(status,(int)response.StatusCode);
        using(var next=await s.GetAsync(prefix+"files/game.bin","a"))Assert.Equal(HttpStatusCode.OK,next.StatusCode);
        using(var denied=await s.GetAsync(prefix+suffix,"b",new(method),range))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        new DistributionTokens(s.Store).Revoke("a");
        using(var revoked=await s.GetAsync(prefix+suffix,"a",new(method),range))Assert.Equal(HttpStatusCode.Unauthorized,revoked.StatusCode);
        using var after=await s.GetAsync(prefix+"files/game.bin","all");Assert.Equal(HttpStatusCode.OK,after.StatusCode);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LimitedBearerHandlerFailureOrDisconnectDoesNotLeakSlot(bool disconnect)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
        await using var s=await ServerFixture.CreateAsync(c=>c.MaxConcurrentDownloads=1,new(){BeforeFile=async c=>
        {
            if(Interlocked.Increment(ref count)!=1)return;entered.SetResult();
            try{if(disconnect)await Task.Delay(Timeout.Infinite,c.RequestAborted);else throw new IOException("owned failure");}finally{exited.SetResult();}
        }});
        var path="/releases/demo/prod/stable/1.0.0/windows-x64/files/game.bin";
        using var cancel=new CancellationTokenSource();var pending=s.GetAsync(path,"a",token:cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(disconnect){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);}
        else{using var failed=await pending;Assert.Equal(HttpStatusCode.ServiceUnavailable,failed.StatusCode);}
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true){using var next=await s.GetAsync(path,"a");if(next.StatusCode!=HttpStatusCode.TooManyRequests){Assert.Equal(HttpStatusCode.OK,next.StatusCode);break;}await Task.Delay(10,timeout.Token);}
    }
    private sealed class ExpiryClock : TimeProvider
    {public DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    [Theory][InlineData("GET","manifest.json")][InlineData("HEAD","manifest.json.sig")][InlineData("HEAD","files/hero.png")][InlineData("GET","files/game.bin")]
    public async Task LimitedBearerExpiryCannotReviveOnClockRollback(string method,string suffix)
    {
        var clock=new ExpiryClock();await using var s=await ServerFixture.CreateAsync(c=>c.MaxConcurrentDownloads=1,new(){Clock=clock});
        s.Tokens["a"]=new DistributionTokens(s.Store,clock).Issue("a",clock.Now.AddSeconds(1));clock.Now=clock.Now.AddSeconds(1);
        var path="/releases/demo/prod/stable/1.0.0/windows-x64/"+suffix;
        using(var expired=await s.GetAsync(path,"a",new(method),suffix=="files/game.bin"?"bytes=2-4":null))Assert.Equal(HttpStatusCode.Unauthorized,expired.StatusCode);
        clock.Now=clock.Now.AddDays(-1);using var reversed=await s.GetAsync(path,"a",new(method));Assert.Equal(HttpStatusCode.Unauthorized,reversed.StatusCode);
    }
    private static async Task<(DistributionCatalog Catalog, DistributionEnvelope Envelope)> ReadCatalog(HttpResponseMessage response)
    {
        var envelope = JsonSerializer.Deserialize<DistributionEnvelope>(await response.Content.ReadAsByteArrayAsync(), JsonFiles.Options)!;
        return (JsonSerializer.Deserialize<DistributionCatalog>(Convert.FromBase64String(envelope.Payload), JsonFiles.Options)!, envelope);
    }
    private static ReleaseSidecar Metadata(string version) => new()
    {
        ProjectId = "demo", Version = version, DisplayName = "Demo", HeroPath = "hero.png", ThumbnailPath = "thumbnail.png"
    };
    private static LauncherManifest Manifest(string heroHash) => new()
    {
        Files = new()
        {
            new() { Path = "hero.png", Sha256 = heroHash, Size = 2 },
            new() { Path = "thumbnail.png", Sha256 = "thumbnail-first", Size = 3 },
            new() { Path = "game.bin", Sha256 = "unused", Size = 10 }
        }
    };
    private static AccessPolicy Policy(string version) => new()
    {
        Clients = new[] { ("a", new List<string> { version }), ("b", new List<string> { "2.0.0" }), ("all", new List<string>()) }
            .Select(item => new ClientAccess
            {
                Id = item.Item1, Addresses = new() { "127.0.0.0/8" }, Grants = new()
                { new() { ProjectId = "demo", Environment = "prod", Channel = "stable", Versions = item.Item2 } }
            }).ToList()
    };
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "distribution-perf-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, true); }
    }
    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory directory;
        private readonly WebApplication app;
        private readonly HttpClient http;
        public IntakeStore Store { get; }
        public Dictionary<string, string> Tokens { get; } = new();
        public bool VerifySignature(DistributionEnvelope envelope)
        {
            using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(Store.Settings.SigningKeyPath));
            var signature = JsonSerializer.Deserialize<DetachedSignatureEnvelope>(envelope.SignatureDocument, JsonFiles.Options)!;
            return key.VerifyData(Convert.FromBase64String(envelope.Payload), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256);
        }
        private ServerFixture(TemporaryDirectory directory, IntakeStore store, WebApplication app)
        {
            this.directory = directory; Store = store; this.app = app;
            http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
            foreach (var client in new[] { "a", "b", "all" }) Tokens.Add(client, new DistributionTokens(store).Issue(client));
        }
        public static async Task<ServerFixture> CreateAsync(Action<DistributionSettings>? configure=null,DistributionHttp.TestHooks? hooks=null)
        {
            var directory = new TemporaryDirectory();
            var settings = new DistributionSettings
            {
                Root = directory.Path, PolicyPath = Path.Combine(directory.Path, "policy.json"),
                SigningKeyPath = Path.Combine(directory.Path, "signing.pem"), ListenUrl = "http://127.0.0.1:0"
            };
            configure?.Invoke(settings);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(settings.SigningKeyPath, key.ExportECPrivateKeyPem());
            await JsonFiles.WriteAsync(settings.PolicyPath, Policy("1.0.0"));
            var store = new IntakeStore(settings);
            foreach (var version in new[] { "2.0.0", "1.0.0" })
            {
                var metadata = Metadata(version);
                var releaseDirectory = Path.Combine(store.Root, "releases", metadata.ReleaseId.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.Combine(releaseDirectory, "files"));
                await JsonFiles.WriteAsync(Path.Combine(releaseDirectory, "manifest.json"), Manifest(version));
                await File.WriteAllTextAsync(Path.Combine(releaseDirectory, "manifest.json.sig"), "signature");
                await File.WriteAllTextAsync(Path.Combine(releaseDirectory, "files", "game.bin"), "abcdefghij", new UTF8Encoding(false));
                using var db = store.Open(); using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO releases VALUES($id,$job,$directory,$metadata)";
                command.Parameters.AddWithValue("$id", metadata.ReleaseId);
                command.Parameters.AddWithValue("$job", version);
                command.Parameters.AddWithValue("$directory", releaseDirectory);
                command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, JsonFiles.Options));
                command.ExecuteNonQuery();
            }
            var promotion = new ReleasePromotions(store);
            var first = promotion.Promote(new("demo", "prod", "stable", "windows-x64", "2.0.0"), 0, "test fixture");
            promotion.Promote(new("demo", "prod", "stable", "windows-x64", "1.0.0"), first.Revision, "test fixture");
            var app = DistributionHttp.CreateApplicationCore(store,hooks);
            await app.StartAsync();
            return new ServerFixture(directory, store, app);
        }
        public async Task<HttpResponseMessage> GetAsync(string path, string? client, HttpMethod? method = null, string? range = null, string? original = null, string? ip = null, CancellationToken token = default)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
            if (client is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[client]);
            if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
            if (original is not null) request.Headers.Add("X-Original-URI", original);
            if (ip is not null) request.Headers.Add("X-Distribution-Client-IP", ip);
            return await http.SendAsync(request, token);
        }
        public async ValueTask DisposeAsync()
        {
            http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); directory.Dispose();
        }
    }
}
