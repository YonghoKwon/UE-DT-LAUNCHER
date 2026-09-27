using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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
        builder.WebHost.UseUrls(store.Settings.ListenUrl);
        var app = builder.Build();
        var publisher = new ApprovedPublisher(store);
        var tokens = new DistributionTokens(store);
        var assets = new PublishedAssetCache();
        var sequenceGate = new SemaphoreSlim(1, 1);
        var databaseGate = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        IAccessPolicyProvider provider = new FileAccessPolicyProvider(store.Settings.PolicyPath);
        app.Lifetime.ApplicationStopped.Register(() => app.Logger.LogInformation("DistributionPerformance {Metrics}",
            JsonSerializer.Serialize(DistributionPerformance.Snapshot())));
        // Let app-scoped coordination be collected: forced shutdown must not dispose a gate while an
        // in-flight synchronous SQLite commit is still about to release it.
        app.Use(async (context, next) =>
        {
            using var measurement = DistributionPerformance.MeasureRequest();
            // Catalogs contain client-specific grants and a fresh anti-replay sequence, including errors.
            if (context.Request.Path == "/api/v1/catalog") context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method is not ("GET" or "HEAD")) { context.Response.StatusCode = 405; return; }
            var authorization = context.Request.Headers.Authorization.ToString();
            string? clientId;
            // SQLite's rollback journal lets short readers delay writer commit. Coordinate only this
            // server's SQL intervals to avoid native busy-poll delays, never caching authorization.
            databaseGate.EnterReadLock();
            try
            {
                clientId = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? tokens.Authenticate(authorization[7..]) : null;
            }
            finally { databaseGate.ExitReadLock(); }
            if (clientId is null) { context.Response.StatusCode = 401; return; }
            var address = context.Connection.RemoteIpAddress;
            // Only the local nginx hop can supply the observed client IP. nginx overwrites this header.
            if (address is not null && IPAddress.IsLoopback(address) &&
                context.Request.Headers.TryGetValue("X-Distribution-Client-IP", out var forwarded))
                address = IPAddress.TryParse(forwarded.ToString(), out var parsed) ? parsed : null;
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
                    await next(); return;
                }
                var path = context.Request.Path.Value ?? "";
                if (path == "/internal/authorize") path = context.Request.Headers["X-Original-URI"].ToString().Split('?')[0];
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
            return Results.Bytes(JsonSerializer.SerializeToUtf8Bytes(new DistributionEnvelope(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), publisher.Sign(payload)), JsonFiles.Options), "application/json");
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
