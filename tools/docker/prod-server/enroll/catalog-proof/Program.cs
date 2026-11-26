using System.Text.Json;
using UeDtLauncher;

var config = await LauncherPaths.LoadResolvedAsync(args.Single(), CancellationToken.None, readOnly: true);
using var http = SecureHttpClientFactory.Create(config);
var catalog = await CatalogResolver.DownloadCatalogAsync(config, http);
var releases = catalog.Projects.SelectMany(project => project.Releases.Select(release => new
{
    project = project.ProjectId, release.Version, release.Platform, release.Environment, release.Channel
})).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { authenticated = config.CatalogAuthenticated, count = releases.Length, releases }));
