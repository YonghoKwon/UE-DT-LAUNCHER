using Xunit;

namespace UeDtLauncher.Tests;

public class PackageTransactionTests
{
    [Fact]
    public void ShouldPreparePackage_SkipsMatchingInstalledHashUnlessRepairing()
    {
        var package = Package("content", new string('a', 64));
        var state = new InstallState
        {
            AppliedPackageHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CONTENT"] = new string('a', 64)
            }
        };

        Assert.False(LauncherEngine.ShouldPreparePackage(package, state, repairMode: false));
        Assert.True(LauncherEngine.ShouldPreparePackage(package, state, repairMode: true));
        Assert.True(LauncherEngine.ShouldPreparePackage(Package("content", new string('b', 64)), state, repairMode: false));
    }

    [Fact]
    public void BuildPreparedPackageFiles_MapsExtractedFilesUnderExtractTo()
    {
        using var temp = new TempDirectory();
        var nestedFile = Path.Combine(temp.Path, "Content", "Paks", "extra.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
        File.WriteAllText(nestedFile, "pak");
        var paths = new HashSet<string>(PathComparer);

        var files = LauncherEngine.BuildPreparedPackageFiles(
            Package("content", new string('a', 64), "Plugins/Extra"),
            temp.Path,
            new HashSet<string>(PathComparer),
            paths);

        var prepared = Assert.Single(files);
        Assert.Equal(
            Path.Combine("Plugins", "Extra", "Content", "Paks", "extra.pak"),
            prepared.RelativeInstallPath);
        Assert.Equal(nestedFile, prepared.SourcePath);
    }

    [Fact]
    public void BuildPreparedPackageFiles_RejectsManifestAndPackageCollisions()
    {
        using var first = new TempDirectory();
        var firstFile = Path.Combine(first.Path, "shared.dat");
        File.WriteAllText(firstFile, "one");
        var package = Package("content", new string('a', 64), ".");
        var manifestPaths = new HashSet<string>(PathComparer) { "shared.dat" };

        Assert.Throws<InvalidOperationException>(() =>
            LauncherEngine.BuildPreparedPackageFiles(
                package,
                first.Path,
                manifestPaths,
                new HashSet<string>(PathComparer)));

        manifestPaths.Clear();
        var packagePaths = new HashSet<string>(PathComparer);
        _ = LauncherEngine.BuildPreparedPackageFiles(package, first.Path, manifestPaths, packagePaths);

        Assert.Throws<InvalidOperationException>(() =>
            LauncherEngine.BuildPreparedPackageFiles(
                Package("other", new string('b', 64), "."),
                first.Path,
                manifestPaths,
                packagePaths));
    }

    private static LauncherPackage Package(string id, string hash, string extractTo = ".") => new()
    {
        Id = id,
        Url = "https://updates.example.com/package.zip",
        Sha256 = hash,
        Size = 3,
        ExtractTo = extractTo,
        Format = "zip",
        Required = true
    };

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uedt-package-transaction-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
