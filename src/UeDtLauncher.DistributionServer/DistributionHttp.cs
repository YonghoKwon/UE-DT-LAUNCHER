using System.Net;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher.Distribution;

public static class DistributionHttp
{
    public static async Task RunAsync(IntakeStore store)
    {
        if (!Uri.TryCreate(store.Settings.ListenUrl, UriKind.Absolute, out var listen) ||
            !IPAddress.TryParse(listen.Host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidDataException("Distribution API must bind to loopback behind nginx.");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(store.Settings.ListenUrl);
        var app = builder.Build();
        var publisher = new ApprovedPublisher(store);
        _ = publisher.List();
        var tokens = new DistributionTokens(store);
        IAccessPolicyProvider provider = new FileAccessPolicyProvider(store.Settings.PolicyPath);
        app.Use(async (context, next) =>
        {
            if (context.Request.Method is not ("GET" or "HEAD")) { context.Response.StatusCode = 405; return; }
            var authorization = context.Request.Headers.Authorization.ToString();
            var clientId = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? tokens.Authenticate(authorization[7..]) : null;
            if (clientId is null) { context.Response.StatusCode = 401; return; }
            var address = context.Connection.RemoteIpAddress;
            // Only the local nginx hop can supply the observed client IP. nginx overwrites this header.
            if (address is not null && IPAddress.IsLoopback(address) &&
                context.Request.Headers.TryGetValue("X-Distribution-Client-IP", out var forwarded))
                address = IPAddress.TryParse(forwarded.ToString(), out var parsed) ? parsed : null;
            try
            {
                var policy = await provider.LoadAsync(context.RequestAborted);
                var client = policy.Clients.SingleOrDefault(c => c.Id == clientId);
                if (client is null || address is null) { context.Response.StatusCode = 403; return; }
                var allowed = publisher.List().Where(r => AccessPolicyEvaluator.Allows(client, address, r.Metadata)).ToList();
                context.Items["allowed"] = allowed;
                if (context.Request.Path == "/api/v1/catalog") { context.Response.Headers.CacheControl = "no-store"; await next(); return; }
                var path = context.Request.Path.Value ?? "";
                if (path == "/internal/authorize") path = context.Request.Headers["X-Original-URI"].ToString().Split('?')[0];
                var release = allowed.FirstOrDefault(r => path.StartsWith("/releases/" + r.ReleaseId + "/", StringComparison.Ordinal));
                if (release is null) { context.Response.StatusCode = 403; return; }
                context.Items["release"] = release;
                await next();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or JsonException)
            {
                context.Response.StatusCode = 503; // Invalid/unavailable policy never grants access.
            }
        });
        app.MapGet("/api/v1/catalog", (HttpContext context) =>
        {
            var releases = (List<PublishedRelease>)context.Items["allowed"]!;
            var now = DateTimeOffset.UtcNow;
            var catalog = new DistributionCatalog
            {
                SchemaVersion = 2, Sequence = NextSequence(store), GeneratedAt = now.ToString("O"),
                IssuedAtUtc = now.ToString("O"), ExpiresAtUtc = now.AddMinutes(10).ToString("O"),
                Projects = releases.GroupBy(r => r.Metadata.ProjectId).Select(group => new DistributionProject
                {
                    ProjectId = group.Key, DisplayName = group.First().Metadata.DisplayName,
                    Hero = Asset(group.Last(), group.Last().Metadata.HeroPath, store.Settings.PublicUrl),
                    Thumbnail = Asset(group.Last(), group.Last().Metadata.ThumbnailPath, store.Settings.PublicUrl),
                    Releases = group.Select(r => new DistributionRelease
                    {
                        Version = r.Metadata.Version, Environment = r.Metadata.Environment, Channel = r.Metadata.Channel,
                        Platform = r.Metadata.Platform, Notes = r.Metadata.Notes,
                        ManifestUrl = store.Settings.PublicUrl.TrimEnd('/') + "/releases/" + r.ReleaseId + "/manifest.json",
                        ManifestSignatureUrl = store.Settings.PublicUrl.TrimEnd('/') + "/releases/" + r.ReleaseId + "/manifest.json.sig",
                        AllowedClientProfiles = new() { "general", "developer" }, IsLatest = false
                    }).ToList()
                }).ToList()
            };
            // Publication order is explicit and monotonic; each track selects its most recently approved release.
            foreach (var project in catalog.Projects)
                foreach (var track in project.Releases.GroupBy(r => (r.Environment, r.Channel, r.Platform))) track.Last().IsLatest = true;
            var payload = JsonSerializer.Serialize(catalog, JsonFiles.Options);
            return Results.Json(new DistributionEnvelope(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), publisher.Sign(payload)), JsonFiles.Options);
        });
        app.MapGet("/internal/authorize", () => Results.NoContent());
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
        var scanner = Task.Run(async () =>
        {
            var token = app.Lifetime.ApplicationStopping;
            try { while (!token.IsCancellationRequested) { await store.ScanAsync(token); await Task.Delay(2000, token); } }
            catch (OperationCanceledException) { }
        });
        await app.RunAsync(); await scanner;
    }
    private static long NextSequence(IntakeStore store)
    {
        using var db = store.Open(); using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS sequence(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER); INSERT OR IGNORE INTO sequence VALUES(1,0); UPDATE sequence SET value=value+1 WHERE id=1 RETURNING value";
        return Convert.ToInt64(command.ExecuteScalar());
    }
    private static RemoteProjectAsset? Asset(PublishedRelease release, string? path, string url)
    {
        if (path is null) return null;
        var manifest = JsonSerializer.Deserialize<LauncherManifest>(File.ReadAllText(Path.Combine(release.Directory, "manifest.json")), JsonFiles.Options)!;
        var file = manifest.Files.SingleOrDefault(f => f.Path == path.Replace('\\', '/'));
        return file is null ? null : new RemoteProjectAsset(url.TrimEnd('/') + "/releases/" + release.ReleaseId + "/files/" + file.Path,
            file.Sha256, file.Size);
    }
}
