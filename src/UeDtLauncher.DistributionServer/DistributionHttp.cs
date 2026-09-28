using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace UeDtLauncher.Distribution;

public static class DistributionHttp
{
    public static async Task RunAsync(IntakeStore store)
    {
        await using var app = CreateApplication(store);
        var scanner = Task.Run(async () =>
        {
            var token = app.Lifetime.ApplicationStopping;
            try { while (!token.IsCancellationRequested) { await store.ScanAsync(token); await Task.Delay(2000, token); } }
            catch (OperationCanceledException) { }
        });
        await app.RunAsync(); await scanner;
    }

    public static WebApplication CreateApplication(IntakeStore store)
    {
        if (!Uri.TryCreate(store.Settings.ListenUrl, UriKind.Absolute, out var listen) ||
            !IPAddress.TryParse(listen.Host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidDataException("Distribution API must bind to loopback behind nginx.");
        var builder = WebApplication.CreateBuilder();
        if (store.Settings.AuthenticationMode is not ("bearer" or "request-signature-v1")) throw new InvalidDataException("Unsupported server authentication mode.");
        var publicUri = new Uri(store.Settings.PublicUrl, UriKind.Absolute);
        if (publicUri.Scheme is not ("http" or "https") || publicUri.UserInfo.Length != 0 || publicUri.AbsolutePath != "/" || publicUri.Query.Length != 0 || publicUri.Fragment.Length != 0)
            throw new InvalidDataException("PublicUrl must be a plain HTTP(S) origin.");
        var signedRequests = store.Settings.AuthenticationMode == "request-signature-v1";
        builder.Services.AddSingleton<AuthenticationProcessLease>(_ => new(store.Root));
        builder.Services.AddSingleton<DeviceAuthenticationState>(_ => new(RequestSignatures.Origin(publicUri)));
        // Never log request targets: the authentication challenge query and headers are not diagnostics.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        builder.WebHost.UseUrls(store.Settings.ListenUrl);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<AuthenticationProcessLease>();
        var authentication = app.Services.GetRequiredService<DeviceAuthenticationState>();
        var publisher = new ApprovedPublisher(store);
        var tokens = new DistributionTokens(store);
        var deviceKeys = new DistributionDeviceKeys(store);
        var assets = new PublishedAssetCache();
        var sequenceGate = new SemaphoreSlim(1, 1);
        var databaseGate = store.DatabaseGate;
        IAccessPolicyProvider provider = new FileAccessPolicyProvider(store.Settings.PolicyPath);
        app.Lifetime.ApplicationStopped.Register(() => app.Logger.LogInformation("DistributionPerformance {Metrics}",
            JsonSerializer.Serialize(DistributionPerformance.Snapshot())));
        // Let shared-store coordination be collected: forced shutdown must not dispose a gate while an
        // in-flight synchronous SQLite commit is still about to release it.
        app.Use(async (context, next) =>
        {
            using var measurement = DistributionPerformance.MeasureRequest();
            // Catalogs contain client-specific grants and a fresh anti-replay sequence, including errors.
            if (context.Request.Path == "/api/v1/catalog") context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method is not ("GET" or "HEAD")) { context.Response.StatusCode = 405; return; }
            var address = context.Connection.RemoteIpAddress;
            if (address is not null && IPAddress.IsLoopback(address) && context.Request.Headers.TryGetValue("X-Distribution-Client-IP", out var forwarded))
                address = forwarded.Count == 1 && IPAddress.TryParse(forwarded[0], out var parsed) ? parsed : null;
            if (signedRequests && context.Request.Path == "/api/v1/auth/challenge")
            {
                context.Response.Headers.CacheControl = "no-store";
                if (context.Request.Method != "GET" || context.Request.Query.Count != 1 || context.Request.Query["keyId"].Count != 1 ||
                    context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                { context.Response.StatusCode = 400; return; }
                if (address is null || !authentication.AllowChallenge(address.ToString())) { context.Response.StatusCode = 429; return; }
                try { await context.Response.WriteAsJsonAsync(new AuthChallenge(authentication.Issue(context.Request.Query["keyId"].ToString())), JsonFiles.Options, context.RequestAborted); }
                catch (ArgumentException) { context.Response.StatusCode = 400; }
                return;
            }
            string? clientId = null;
            RequestSignatureContext? proof = null;
            long challengeDeadline = 0;
            // SQLite's rollback journal lets short readers delay writer commit. Coordinate only this
            // server's SQL intervals to avoid native busy-poll delays, never caching authorization.
            try
            {
                if (signedRequests)
                {
                    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding") ||
                        context.Request.Headers.Keys.Any(k => k.StartsWith("If-", StringComparison.OrdinalIgnoreCase)) || context.Request.Headers.ContainsKey("Authorization"))
                        throw new InvalidDataException("Unsupported signed request.");
                    static string Header(HttpContext c, string name)
                    {
                        var values = c.Request.Headers[name];
                        return values.Count == 1 && values[0]!.Length <= 2048 ? values[0]! : throw new InvalidDataException("Missing or duplicate authentication header.");
                    }
                    var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
                    if (raw.Length > 8192 || !raw.StartsWith('/') || raw.StartsWith("//", StringComparison.Ordinal) || raw.Contains('#')) throw new InvalidDataException("Invalid request target.");
                    var target = RequestSignatures.Origin(publicUri) + raw;
                    var challenge = Header(context, RequestSignatures.ChallengeHeader);
                    var range = context.Request.Headers.ContainsKey("Range") ? Header(context, "Range") : null;
                    proof = RequestSignatures.Parse(Header(context, "Signature-Input"), context.Request.Method, target, range is not null);
                    var registered = deviceKeys.Find(proof.KeyId);
                    if (registered is null || registered.Revoked || !RequestSignatures.Verify(proof, challenge, range, Header(context, "Signature"), registered.PublicKeyPem))
                    { context.Response.StatusCode = 401; return; }
                    if (!authentication.TryValidate(challenge, proof.KeyId, out challengeDeadline))
                    { context.Response.StatusCode = 401; context.Response.Headers["X-UE-DT-Auth-Error"] = "stale-challenge"; return; }
                    clientId = registered.ClientId;
                    context.Items["request-proof"] = proof;
                }
                else
                {
                    if (publicUri.Scheme != "https") { context.Response.StatusCode = 401; context.Response.Headers["X-UE-DT-Auth-Error"] = "https-bearer-required"; return; }
                    var values = context.Request.Headers.Authorization;
                    var authorization = values.Count == 1 ? values[0]! : "";
                    clientId = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? tokens.Authenticate(authorization[7..]) : null;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or CryptographicException)
            { context.Response.StatusCode = 401; return; }
            catch (SqliteException) { context.Response.StatusCode = 503; return; }
            if (clientId is null) { context.Response.StatusCode = 401; return; }
            bool ConsumeProof()
            {
                if (proof is null) return true;
                var error = authentication.Consume(proof.KeyId, proof.Nonce, challengeDeadline);
                if (error is null) return true;
                context.Response.StatusCode = error == "auth-capacity" ? 429 : 401;
                context.Response.Headers["X-UE-DT-Auth-Error"] = error; return false;
            }
            try
            {
                var policy = await provider.LoadCompiledAsync(context.RequestAborted);
                var client = policy.FindClient(clientId);
                if (client is null || address is null || !client.AllowsAddress(address)) { context.Response.StatusCode = 403; return; }
                if (context.Request.Path == "/api/v1/catalog")
                {
                    List<PublishedRelease> releases;
                    databaseGate.EnterReadLock();
                    try { releases = publisher.List(); }
                    finally { databaseGate.ExitReadLock(); }
                    context.Items["allowed"] = releases.Where(r => client.AllowsRelease(r.Metadata)).ToList();
                    if (!ConsumeProof()) return;
                    await next(); return;
                }
                var path = context.Request.Path.Value ?? "";
                if (path == "/internal/authorize")
                {
                    if (signedRequests) { context.Response.StatusCode = 404; return; }
                    path = context.Request.Headers["X-Original-URI"].ToString().Split('?')[0];
                }
                PublishedRelease? release = null;
                if (TryGetReleaseId(path, out var releaseId))
                {
                    databaseGate.EnterReadLock();
                    try { release = publisher.Find(releaseId); }
                    finally { databaseGate.ExitReadLock(); }
                }
                if (release is null || !client.AllowsRelease(release.Metadata))
                {
                    databaseGate.EnterWriteLock();
                    try { store.Audit("download-denied", clientId); }
                    finally { databaseGate.ExitWriteLock(); }
                    context.Response.StatusCode = 403; return;
                }
                context.Items["release"] = release;
                if (!ConsumeProof()) return;
                await next();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException or UnauthorizedAccessException)
            {
                context.Response.StatusCode = 503; // Invalid/unavailable policy never grants access.
            }
        });
        app.MapMethods("/api/v1/catalog", new[] { "GET", "HEAD" }, async (HttpContext context) =>
        {
            var releases = (List<PublishedRelease>)context.Items["allowed"]!;
            var sequence = await NextSequenceAsync(store, sequenceGate, databaseGate, context.RequestAborted);
            var now = DateTimeOffset.UtcNow;
            var catalog = new DistributionCatalog
            {
                SchemaVersion = 2, Sequence = sequence, GeneratedAt = now.ToString("O"),
                IssuedAtUtc = now.ToString("O"), ExpiresAtUtc = now.AddMinutes(10).ToString("O"),
                Projects = releases.GroupBy(r => r.Metadata.ProjectId).Select(group =>
                {
                    var latest = group.Last();
                    var projectAssets = assets.Get(latest);
                    return new DistributionProject
                    {
                        ProjectId = group.Key, DisplayName = group.First().Metadata.DisplayName,
                        Hero = Asset(latest, projectAssets.Hero, store.Settings.PublicUrl),
                        Thumbnail = Asset(latest, projectAssets.Thumbnail, store.Settings.PublicUrl),
                        Releases = group.Select(r => new DistributionRelease
                        {
                            Version = r.Metadata.Version, Environment = r.Metadata.Environment, Channel = r.Metadata.Channel,
                            Platform = r.Metadata.Platform, Notes = r.Metadata.Notes,
                            ManifestUrl = store.Settings.PublicUrl.TrimEnd('/') + "/releases/" + r.ReleaseId + "/manifest.json",
                            ManifestSignatureUrl = store.Settings.PublicUrl.TrimEnd('/') + "/releases/" + r.ReleaseId + "/manifest.json.sig",
                            AllowedClientProfiles = new() { "general", "developer" }, IsLatest = false
                        }).ToList()
                    };
                }).ToList()
            };
            // Publication order is explicit and monotonic; each track selects its most recently approved release.
            foreach (var project in catalog.Projects)
                foreach (var track in project.Releases.GroupBy(r => (r.Environment, r.Channel, r.Platform))) track.Last().IsLatest = true;
            var payload = JsonSerializer.Serialize(catalog, JsonFiles.Options);
            CatalogRequestBinding? binding = null;
            if (context.Items["request-proof"] is RequestSignatureContext proof)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
                var signature = publisher.Sign(Encoding.UTF8.GetString(RequestSignatures.BindingBytes(proof, hash)));
                binding = new(proof.KeyId, proof.Nonce, proof.Method, proof.TargetUri, hash, signature);
            }
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(new DistributionEnvelope(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), publisher.Sign(payload), binding), JsonFiles.Options), "application/json");
        });
        app.MapMethods("/internal/authorize", new[] { "GET", "HEAD" }, () => Results.NoContent());
        app.MapMethods("/releases/{project}/{environment}/{channel}/{version}/{platform}/{**path}", new[] { "GET", "HEAD" },
            (HttpContext context, string path) =>
            {
                var release = (PublishedRelease)context.Items["release"]!;
                if (path is not ("manifest.json" or "manifest.json.sig") && !path.StartsWith("files/", StringComparison.Ordinal))
                    return Results.NotFound();
                try
                {
                    ReleaseSidecar.Relative(path);
                    var file = SafePath.ResolveInsideChecked(release.Directory, path);
                    return File.Exists(file) ? Results.File(file, "application/octet-stream", enableRangeProcessing: true) : Results.NotFound();
                }
                catch (InvalidOperationException) { return Results.NotFound(); }
                catch (InvalidDataException) { return Results.NotFound(); }
            });
        return app;
    }
    private static async Task<long> NextSequenceAsync(IntakeStore store, SemaphoreSlim gate, ReaderWriterLockSlim databaseGate, CancellationToken token)
    {
        // Only serialize local sequence writes; authentication, policy reads and signing stay request-local.
        using (DistributionPerformance.MeasureSequenceWait()) await gate.WaitAsync(token);
        try
        {
            databaseGate.EnterWriteLock();
            try
            {
                using var measurement = DistributionPerformance.MeasureDatabase("catalog-sequence");
                using var db = store.Open();
                // RETURNING's implicit commit can raise SQLITE_BUSY while its reader is disposed. An explicit
                // short transaction puts commit on the provider's normal busy-retry path, without changing WAL.
                using var transaction = db.BeginTransaction(deferred: false);
                using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE sequence SET value=value+1 WHERE id=1 RETURNING value";
                var sequence = Convert.ToInt64(command.ExecuteScalar() ?? throw new InvalidDataException("Missing catalog sequence."));
                transaction.Commit();
                return sequence;
            }
            finally { databaseGate.ExitWriteLock(); }
        }
        catch (SqliteException ex) { DistributionPerformance.RecordDatabaseBusy(ex.SqliteErrorCode); throw; }
        finally { gate.Release(); }
    }
    public static bool TryGetReleaseId(string path, out string releaseId)
    {
        releaseId = "";
        if (!path.StartsWith("/releases/", StringComparison.Ordinal)) return false;
        var parts = path.Split('/', 8);
        if (parts.Length != 8 || parts[7].Length == 0) return false;
        try { for (var i = 2; i < 7; i++) ReleaseSidecar.Segment(parts[i]); }
        catch (InvalidDataException) { return false; }
        releaseId = string.Join('/', parts, 2, 5);
        return true;
    }
    private static RemoteProjectAsset? Asset(PublishedRelease release, PublishedAssetMetadata? asset, string url) =>
        asset is null ? null : new RemoteProjectAsset(url.TrimEnd('/') + "/releases/" + release.ReleaseId + "/files/" + asset.Path,
            asset.Sha256, asset.Size);
}
