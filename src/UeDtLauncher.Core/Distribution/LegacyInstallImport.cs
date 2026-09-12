namespace UeDtLauncher;

public sealed record InstallImportPlan(string Source, string Destination, string Version, int Files, bool Applied);
public static class LegacyInstallImport
{
    public static async Task<InstallImportPlan> RunAsync(string configPath, string destinationRoot, bool apply, CancellationToken token = default)
    {
        var config = await JsonFiles.ReadAsync<LauncherConfig>(configPath, token);
        LauncherPaths.ResolveInPlace(config, configPath);
        var manifest = await JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath, token);
        LauncherEngine.ValidateManifest(manifest, config);
        var selection = new ReleaseSelection(manifest.AppId, config.Environment, config.Channel, manifest.Platform, manifest.Version);
        selection.Validate();
        var destination = SafePath.ResolveInside(Path.GetFullPath(destinationRoot), selection.ReleaseId);
        if (Directory.Exists(destination)) throw new IOException("Import destination already exists.");
        var files = Directory.EnumerateFiles(config.InstallDir, "*", SearchOption.AllDirectories).ToList();
        foreach (var file in manifest.Files)
            if (!await Hashing.Sha256MatchesAsync(SafePath.ResolveInsideChecked(config.InstallDir, file.Path), file.Sha256, token))
                throw new InvalidDataException("Legacy files do not match installed manifest.");
        if (apply)
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(config.InstallDir, file);
                var source = SafePath.ResolveInsideChecked(config.InstallDir, relative);
                var target = SafePath.ResolveInsideChecked(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, false);
            }
            // Original files, including user data, are retained. Import never removes or overwrites them.
        }
        return new(config.InstallDir, destination, manifest.Version, files.Count, apply);
    }
}
