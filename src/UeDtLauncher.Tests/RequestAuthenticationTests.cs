using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class RequestAuthenticationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientRenewsExpiredChallengeOnce_UsesFreshNonceAndPreservesRange(bool alwaysStale)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = LauncherConfigurationTemplates.Distribution(new(ServerUrl: "http://127.0.0.1:18500", DeploymentMode: "portable"));
        var transport = new StaleChallengeTransport(alwaysStale);
        using var http = new HttpClient(new DeviceSignatureHandler(config, transport, new(1, "pc", key.ExportPkcs8PrivateKeyPem())));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:18500/releases/file"); request.Headers.Range = new(3, null);
        using var response = await http.SendAsync(request);
        Assert.Equal(alwaysStale ? HttpStatusCode.Unauthorized : HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(2, transport.Challenges); Assert.Equal(2, transport.Requests.Count);
        Assert.NotEqual(transport.Requests[0].Nonce, transport.Requests[1].Nonce);
        Assert.All(transport.Ranges, value => Assert.Equal("bytes=3-", value));
        Assert.True(response.RequestMessage!.Options.TryGetValue(RequestSignatures.ContextKey, out var successfulAttempt));
        Assert.Equal(transport.Requests[1], successfulAttempt);
    }

    private sealed class StaleChallengeTransport(bool alwaysStale) : HttpMessageHandler
    {
        public int Challenges;
        public List<RequestSignatureContext> Requests = new();
        public List<string?> Ranges = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/challenge")
            {
                Challenges++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthChallenge("challenge-" + Challenges), options: JsonFiles.Options) });
            }
            Assert.Null(request.Headers.Authorization);
            Assert.True(request.Options.TryGetValue(RequestSignatures.ContextKey, out var context)); Requests.Add(context!); Ranges.Add(request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(alwaysStale || Requests.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent([]) };
            if (response.StatusCode == HttpStatusCode.Unauthorized) response.Headers.Add("X-UE-DT-Auth-Error", "stale-challenge");
            return Task.FromResult(response);
        }
    }
    [Fact]
    public void SignatureBase_UsesRfc9421ComponentsAndParameters()
    {
        var context = new RequestSignatureContext("pc", new string('A', 43), "GET", "http://127.0.0.1/a%20b?q=one%2Ftwo");
        var text = Encoding.UTF8.GetString(RequestSignatures.SignatureBase(context, "abc.def", "bytes=3-"));
        Assert.StartsWith("\"@method\": GET\n\"@target-uri\": http://127.0.0.1/a%20b?q=one%2Ftwo\n\"x-ue-dt-challenge\": abc.def\n\"range\": bytes=3-\n", text);
        Assert.EndsWith(";keyid=\"pc\";nonce=\"" + new string('A', 43) + "\";alg=\"ecdsa-p256-sha256\";tag=\"ue-dt-request-v1\"", text);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = RequestSignatures.Sign(context, "abc.def", "bytes=3-", key.ExportPkcs8PrivateKeyPem());
        Assert.True(RequestSignatures.Verify(context, "abc.def", "bytes=3-", signed.Signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.False(RequestSignatures.Verify(context with { Method = "HEAD" }, "abc.def", "bytes=3-", signed.Signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<InvalidDataException>(() => RequestSignatures.Parse(signed.Input, "GET", context.TargetUri, false));
    }

    [Fact]
    public async Task NonceIsAtomic_BoundedAcrossChallenges_ExpiresMonotonically()
    {
        var clock = new MonotonicClock();
        using var state = new DeviceAuthenticationState("http://localhost", clock, 3, 2);
        var challenge = state.Issue("pc"); Assert.True(state.TryValidate(challenge, "pc", out var expiry));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => state.Consume("pc", "nonce", expiry))));
        Assert.Single(outcomes, o => o is null);
        Assert.Null(state.Consume("pc", "second", expiry));
        Assert.True(state.TryValidate(state.Issue("pc"), "pc", out var renewed));
        Assert.Equal("auth-capacity", state.Consume("pc", "third", renewed));
        Assert.Equal("replayed-request", state.Consume("pc", "nonce", expiry));
        clock.Utc = clock.Utc.AddDays(-30);
        Assert.True(state.TryValidate(challenge, "pc", out _));
        clock.Advance(61);
        Assert.False(state.TryValidate(challenge, "pc", out _));
        Assert.True(state.TryValidate(state.Issue("pc"), "pc", out var next));
        Assert.Null(state.Consume("pc", "third", next));
        using var restarted = new DeviceAuthenticationState("http://localhost", clock);
        Assert.False(restarted.TryValidate(state.Issue("pc"), "pc", out _));
        Assert.False(state.TryValidate(state.Issue("pc"), "different-key", out _));
    }

    [Fact]
    public async Task CatalogIsBoundToDeviceAndExactRequest_AndReplayIsRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var challenge = await fixture.Challenge(); var request = fixture.Proof("/api/v1/catalog");
        using var response = await fixture.Send(request, challenge);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = (await response.Content.ReadFromJsonAsync<DistributionEnvelope>(JsonFiles.Options))!;
        RequestSignatures.VerifyBinding(envelope, fixture.Config, request);
        Assert.Throws<CryptographicException>(() => RequestSignatures.VerifyBinding(envelope, fixture.Config, request with { Nonce = RequestSignatures.NewNonce() }));
        Assert.Throws<CryptographicException>(() => RequestSignatures.VerifyBinding(envelope with { RequestBinding = null }, fixture.Config, request));
        Assert.Throws<CryptographicException>(() => RequestSignatures.VerifyBinding(envelope with { Payload = Convert.ToBase64String([1, 2]) }, fixture.Config, request));
        using var replay = await fixture.Send(request, challenge); Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var next = await fixture.Send(fixture.Proof("/api/v1/catalog"), challenge); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task FileAndRangeRequireCurrentPolicyAndKey_NoDeveloperPrivilege()
    {
        await using var fixture = await Fixture.CreateAsync();
        var challenge = await fixture.Challenge();
        var path = "/releases/demo/prod/stable/1.0.0/windows-x64/files/game.bin";
        using var full = await fixture.Send(fixture.Proof(path), challenge); Assert.Equal("abcdefghij", await full.Content.ReadAsStringAsync());
        using var range = await fixture.Send(fixture.Proof(path), challenge, "bytes=3-");
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode); Assert.Equal("defghij", await range.Content.ReadAsStringAsync());
        using var deniedIp = await fixture.Send(fixture.Proof(path), challenge, ip: "10.200.0.99"); Assert.Equal(HttpStatusCode.Forbidden, deniedIp.StatusCode);
        await File.WriteAllTextAsync(fixture.Store.Settings.PolicyPath, "invalid policy");
        using var invalid = await fixture.Send(fixture.Proof(path), challenge); Assert.Equal(HttpStatusCode.ServiceUnavailable, invalid.StatusCode);
        await fixture.Policy(false);
        using var noGrant = await fixture.Send(fixture.Proof(path), challenge); Assert.Equal(HttpStatusCode.Forbidden, noGrant.StatusCode);
        await fixture.Policy(true);
        new DistributionDeviceKeys(fixture.Store).Revoke("pc-key");
        using var revoked = await fixture.Send(fixture.Proof(path), challenge); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Throws<SqliteException>(() => new DistributionDeviceKeys(fixture.Store).Add("other", fixture.Public));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("query")]
    [InlineData("range")]
    [InlineData("origin")]
    [InlineData("key")]
    [InlineData("conditional")]
    [InlineData("duplicate")]
    public async Task ChangedOrAmbiguousRequestsAreRejected(string mutation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var challenge = await fixture.Challenge(); var proof = fixture.Proof("/api/v1/catalog");
        using var outgoing = fixture.Message(proof, challenge);
        switch (mutation)
        {
            case "path": outgoing.RequestUri = new Uri(fixture.Http.BaseAddress!, "/releases/other"); break;
            case "query": outgoing.RequestUri = new Uri(fixture.Http.BaseAddress!, "/api/v1/catalog?developer=true"); break;
            case "method": outgoing.Method = HttpMethod.Head; break;
            case "range": outgoing.Headers.Range = new(0, 1); break;
            case "origin":
            case "key":
                outgoing.Headers.Remove("Signature-Input"); outgoing.Headers.Remove("Signature");
                var changed = mutation == "origin" ? proof with { TargetUri = "http://other.invalid/api/v1/catalog" } : proof with { KeyId = "unregistered" };
                var signature = RequestSignatures.Sign(changed, challenge, null, fixture.Private);
                outgoing.Headers.Add("Signature-Input", signature.Input); outgoing.Headers.Add("Signature", signature.Signature); break;
            case "conditional": outgoing.Headers.TryAddWithoutValidation("If-None-Match", "anything"); break;
            case "duplicate": outgoing.Headers.TryAddWithoutValidation("Signature", "sig1=:duplicate:"); break;
        }
        using var response = await fixture.Http.SendAsync(outgoing);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HttpBearerCannotAuthenticateSignatureEndpoint_OrHttpLegacyServer()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog");
        request.Headers.Authorization = new("Bearer", new DistributionTokens(fixture.Store).Issue("pc"));
        using var response = await fixture.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class MonotonicClock : TimeProvider
    {
        private long timestamp = 100_000;
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Advance(int seconds) => timestamp += seconds * 1000;
    }
    [Fact]
    public async Task HeldDownloadReturns429ThenReleasesSlotWithoutAuthorizationCaching()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks=new DistributionHttp.TestHooks{BeforeFile=async context=>{entered.TrySetResult();await release.Task.WaitAsync(context.RequestAborted);}};
        await using var fixture=await Fixture.CreateAsync(settings=>settings.MaxConcurrentDownloads=1,hooks);
        var challenge=await fixture.Challenge();var path="/releases/demo/prod/stable/1.0.0/windows-x64/files/game.bin";
        var active=fixture.Send(fixture.Proof(path),challenge,"bytes=0-2");await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using(var rejected=await fixture.Send(fixture.Proof(path),challenge)){Assert.Equal(HttpStatusCode.TooManyRequests,rejected.StatusCode);Assert.Equal(1,rejected.Headers.RetryAfter!.Delta!.Value.TotalSeconds);}
        release.SetResult();using(var completed=await active)Assert.Equal(HttpStatusCode.PartialContent,completed.StatusCode);
        new DistributionDeviceKeys(fixture.Store).Revoke("pc-key");
        using var unauthorized=await fixture.Send(fixture.Proof(path),challenge);Assert.Equal(HttpStatusCode.Unauthorized,unauthorized.StatusCode);
    }
    [Fact]
    public async Task RateWindowUsesMonotonicClockAndRecoversAfter429()
    {
        var clock=new MonotonicClock();await using var fixture=await Fixture.CreateAsync(settings=>settings.MaxApiRequestsPerSecond=2,new(){Clock=clock});
        var challenge=await fixture.Challenge();using(var first=await fixture.Send(fixture.Proof("/api/v1/catalog"),challenge))Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using(var excess=await fixture.Send(fixture.Proof("/api/v1/catalog"),challenge))Assert.Equal(HttpStatusCode.TooManyRequests,excess.StatusCode);
        clock.Utc=clock.Utc.AddYears(-10);clock.Advance(1);
        using var recovered=await fixture.Send(fixture.Proof("/api/v1/catalog"),challenge);Assert.Equal(HttpStatusCode.OK,recovered.StatusCode);
    }
    [Theory][InlineData("/api/v1/catalog")][InlineData("/releases/demo/prod/stable/1.0.0/windows-x64/manifest.json")][InlineData("/releases/demo/prod/stable/1.0.0/windows-x64/files/game.bin")]
    public async Task ExpiryIsImmediateAcrossSignedEndpointsAndCannotRevive(string path)
    {
        var clock=new MonotonicClock();await using var fixture=await Fixture.CreateAsync(hooks:new(){Clock=clock},expiresAt:clock.Utc.AddSeconds(1));
        var challenge=await fixture.Challenge();clock.Utc=clock.Utc.AddSeconds(1);
        using(var expired=await fixture.Send(fixture.Proof(path),challenge))Assert.Equal(HttpStatusCode.Unauthorized,expired.StatusCode);
        clock.Utc=clock.Utc.AddDays(-1);
        using var rollback=await fixture.Send(fixture.Proof(path),challenge);Assert.Equal(HttpStatusCode.Unauthorized,rollback.StatusCode);
    }

    [Theory]
    [InlineData("GET", "game.bin", null, 200)]
    [InlineData("HEAD", "game.bin", null, 200)]
    [InlineData("GET", "hero.png", null, 200)]
    [InlineData("HEAD", "hero.png", null, 200)]
    [InlineData("GET", "missing.bin", null, 404)]
    [InlineData("GET", "game.bin", "bytes=100-", 416)]
    [InlineData("GET", "game.bin", "bytes=2-4", 206)]
    public async Task LimitedSignedFileOutcomesReturnSlotAndKeepRevocationFresh(string method,string file,string? range,int status)
    {
        await using var f=await Fixture.CreateAsync(s=>s.MaxConcurrentDownloads=1);
        await File.WriteAllTextAsync(Path.Combine(f.Root,"releases/demo/prod/stable/1.0.0/windows-x64/files/hero.png"),"image");
        var challenge=await f.Challenge();var prefix="/releases/demo/prod/stable/1.0.0/windows-x64/files/";
        using(var response=await f.Send(f.Proof(prefix+file) with{Method=method},challenge,range))
        {Assert.Equal(status,(int)response.StatusCode);if(method=="HEAD")Assert.Empty(await response.Content.ReadAsByteArrayAsync());}
        using(var next=await f.Send(f.Proof(prefix+"game.bin"),challenge))Assert.Equal(HttpStatusCode.OK,next.StatusCode);
        await f.Policy(false);
        using(var denied=await f.Send(f.Proof(prefix+file) with{Method=method},challenge,range))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await f.Policy(true);new DistributionDeviceKeys(f.Store).Revoke("pc-key");
        using var revoked=await f.Send(f.Proof(prefix+file) with{Method=method},challenge,range);Assert.Equal(HttpStatusCode.Unauthorized,revoked.StatusCode);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task AbortedAndFailedFileHandlersReleaseLimitedSlot(bool disconnect)
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
        var hooks=new DistributionHttp.TestHooks{BeforeFile=async c=>
        {
            if(Interlocked.Increment(ref count)!=1)return;
            entered.SetResult();
            try {if(disconnect)await Task.Delay(Timeout.Infinite,c.RequestAborted);else throw new IOException("owned handler failure");}
            finally{exited.SetResult();}
        }};
        await using var f=await Fixture.CreateAsync(s=>s.MaxConcurrentDownloads=1,hooks);
        var challenge=await f.Challenge();var path="/releases/demo/prod/stable/1.0.0/windows-x64/files/game.bin";
        using var message=f.Message(f.Proof(path),challenge);using var cancel=new CancellationTokenSource();
        var pending=f.Http.SendAsync(message,cancel.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(disconnect){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);}
        else{using var failed=await pending;Assert.Equal(HttpStatusCode.ServiceUnavailable,failed.StatusCode);}
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Handler exit precedes middleware disposal; bounded fresh requests observe actual slot return.
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true)
        {
            using var next=await f.Send(f.Proof(path),challenge);
            if(next.StatusCode!=HttpStatusCode.TooManyRequests){Assert.Equal(HttpStatusCode.OK,next.StatusCode);break;}
            await Task.Delay(10,timeout.Token);
        }
    }
    [Theory][InlineData("GET","manifest.json")][InlineData("HEAD","manifest.json.sig")][InlineData("HEAD","files/hero.png")][InlineData("GET","files/game.bin")]
    public async Task ExpiredKeyWithLimitsCannotAccessMetadataImagesOrRange(string method,string suffix)
    {
        var clock=new MonotonicClock();await using var f=await Fixture.CreateAsync(s=>s.MaxConcurrentDownloads=1,new(){Clock=clock},clock.Utc.AddSeconds(1));
        var challenge=await f.Challenge();clock.Utc=clock.Utc.AddSeconds(1);
        var proof=f.Proof("/releases/demo/prod/stable/1.0.0/windows-x64/"+suffix) with{Method=method};
        using(var expired=await f.Send(proof,challenge,suffix=="files/game.bin"?"bytes=2-4":null))Assert.Equal(HttpStatusCode.Unauthorized,expired.StatusCode);
        clock.Utc=clock.Utc.AddDays(-1);
        using var reversed=await f.Send(proof with{Nonce=RequestSignatures.NewNonce()},challenge);Assert.Equal(HttpStatusCode.Unauthorized,reversed.StatusCode);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-auth-" + Guid.NewGuid().ToString("N"));
        public IntakeStore Store = null!;
        public LauncherConfig Config = null!;
        public DevicePublicKey Public = null!;
        public string Private = "";
        public HttpClient Http = null!;
        private WebApplication app = null!;
        public static async Task<Fixture> CreateAsync(Action<DistributionSettings>? configure=null,DistributionHttp.TestHooks? hooks=null,DateTimeOffset? expiresAt=null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            var settings = new DistributionSettings { Root = f.Root, AuthenticationMode = "request-signature-v1", PublicUrl = "http://127.0.0.1:18500", ListenUrl = "http://127.0.0.1:0", PolicyPath = Path.Combine(f.Root, "policy.json"), SigningKeyPath = Path.Combine(f.Root, "release.pem") };
            configure?.Invoke(settings);
            using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await File.WriteAllTextAsync(settings.SigningKeyPath, releaseKey.ExportPkcs8PrivateKeyPem());
            var publicPath = Path.Combine(f.Root, "public.pem"); await File.WriteAllTextAsync(publicPath, releaseKey.ExportSubjectPublicKeyInfoPem());
            f.Store = new(settings); await f.Policy(true);
            f.Config = new(); f.Config.Security.TrustedSigningKeys.Add(new() { KeyId = settings.SigningKeyId, PublicKeyPath = publicPath });
            using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256); f.Private = device.ExportPkcs8PrivateKeyPem();
            f.Public = new(1, "pc-key", device.ExportSubjectPublicKeyInfoPem()); new DistributionDeviceKeys(f.Store).Add("pc", f.Public,expiresAt);
            var metadata = new ReleaseSidecar { ProjectId = "demo", Version = "1.0.0" };
            var directory = Path.Combine(f.Root, "releases", metadata.ReleaseId);
            Directory.CreateDirectory(Path.Combine(directory, "files")); await File.WriteAllTextAsync(Path.Combine(directory, "files", "game.bin"), "abcdefghij");
            using (var db = f.Store.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "INSERT INTO releases VALUES($id,'test',$dir,$metadata)";
                command.Parameters.AddWithValue("$id", metadata.ReleaseId); command.Parameters.AddWithValue("$dir", directory); command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(metadata, JsonFiles.Options)); command.ExecuteNonQuery();
            }
            new ReleasePromotions(f.Store).Promote(new(metadata.ProjectId, metadata.Environment, metadata.Channel, metadata.Platform, metadata.Version), 0, "test fixture");
            f.app = DistributionHttp.CreateApplicationCore(f.Store,hooks); await f.app.StartAsync();
            f.Http = new() { BaseAddress = new Uri(f.app.Urls.Single()) }; return f;
        }
        public Task Policy(bool allowed) => JsonFiles.WriteAsync(Store.Settings.PolicyPath, new AccessPolicy { Clients = [new ClientAccess { Id = "pc", Addresses = ["127.0.0.0/8"], Grants = allowed ? [new ReleaseGrant { ProjectId = "demo", Environment = "prod", Channel = "stable" }] : [] }] });
        public async Task<string> Challenge() => (await Http.GetFromJsonAsync<AuthChallenge>("/api/v1/auth/challenge?keyId=pc-key", JsonFiles.Options))!.Challenge;
        public RequestSignatureContext Proof(string path) => new("pc-key", RequestSignatures.NewNonce(), "GET", Store.Settings.PublicUrl + path);
        public HttpRequestMessage Message(RequestSignatureContext proof, string challenge, string? range = null, string? ip = null)
        {
            var message = new HttpRequestMessage(new HttpMethod(proof.Method), new Uri(proof.TargetUri).PathAndQuery);
            var signed = RequestSignatures.Sign(proof, challenge, range, Private);
            message.Headers.Add("Signature-Input", signed.Input); message.Headers.Add("Signature", signed.Signature); message.Headers.Add(RequestSignatures.ChallengeHeader, challenge);
            if (range is not null) message.Headers.TryAddWithoutValidation("Range", range);
            if (ip is not null) message.Headers.Add("X-Distribution-Client-IP", ip);
            return message;
        }
        public async Task<HttpResponseMessage> Send(RequestSignatureContext proof, string challenge, string? range = null, string? ip = null)
        { using var message = Message(proof, challenge, range, ip); return await Http.SendAsync(message); }
        public async ValueTask DisposeAsync()
        { Http.Dispose(); await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
}
