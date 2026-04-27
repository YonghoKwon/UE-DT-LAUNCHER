namespace UeDtLauncher;

public static class ManifestGenerator
{
    public static async Task GenerateAsync(string packageDir, string outputPath, string baseUrl, string entryPoint, string version, string channel, string platform, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(packageDir);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Package directory does not exist: {root}");
        }

        var manifest = new LauncherManifest
        {
            AppId = "ue-dt-app",
            Version = version,
            Channel = channel,
            Platform = platform,
            EntryPoint = NormalizeManifestPath(entryPoint),
            BaseUrl = baseUrl.TrimEnd('/')
        };

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(Path.GetFileName(outputPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeManifestPath(Path.GetRelativePath(root, file));
            var info = new FileInfo(file);
            manifest.Files.Add(new ManifestFile
            {
                Path = relativePath,
                Url = relativePath,
                Size = info.Length,
                Sha256 = await Hashing.Sha256FileAsync(file, cancellationToken),
                Executable = IsLikelyExecutable(relativePath)
            });
        }

        if (!manifest.Files.Any(file => string.Equals(file.Path, manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Entry point was not found in package directory: {entryPoint}");
        }

        await JsonFiles.WriteAsync(outputPath, manifest, cancellationToken);
        Console.WriteLine($"Manifest generated: {outputPath}");
        Console.WriteLine($"Files: {manifest.Files.Count}");
    }

    private static string NormalizeManifestPath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool IsLikelyExecutable(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
               || string.IsNullOrEmpty(extension) && relativePath.Contains("/Binaries/", StringComparison.OrdinalIgnoreCase);
    }
}
