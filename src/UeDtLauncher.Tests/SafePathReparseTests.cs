using Xunit;

namespace UeDtLauncher.Tests;

public class SafePathReparseTests : IDisposable
{
    private readonly string _workDir;

    public SafePathReparseTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "uedt-reparse-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void ResolveInsideChecked_AllowsPlainDirectories()
    {
        var root = Path.Combine(_workDir, "install");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var resolved = SafePath.ResolveInsideChecked(root, "sub/file.txt");
        Assert.StartsWith(Path.GetFullPath(root), resolved);
    }

    [Fact]
    public void ResolveInsideChecked_RejectsSymlinkedSubdirectory()
    {
        if (OperatingSystem.IsWindows()) return; // creating symlinks on Windows requires elevation

        var root = Path.Combine(_workDir, "install");
        var outside = Path.Combine(_workDir, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        Assert.Throws<InvalidOperationException>(() => SafePath.ResolveInsideChecked(root, "link/file.txt"));
    }

    [Fact]
    public void ResolveInsideChecked_RejectsSymlinkedFileTarget()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(_workDir, "install");
        Directory.CreateDirectory(root);
        var outsideFile = Path.Combine(_workDir, "secret.txt");
        File.WriteAllText(outsideFile, "secret");
        File.CreateSymbolicLink(Path.Combine(root, "alias.txt"), outsideFile);

        Assert.Throws<InvalidOperationException>(() => SafePath.ResolveInsideChecked(root, "alias.txt"));
    }

    [Fact]
    public void ResolveInsideChecked_AllowsSymlinkedRootItself()
    {
        if (OperatingSystem.IsWindows()) return;

        var realRoot = Path.Combine(_workDir, "real-install");
        Directory.CreateDirectory(realRoot);
        var linkRoot = Path.Combine(_workDir, "linked-install");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        var resolved = SafePath.ResolveInsideChecked(linkRoot, "file.txt");
        Assert.StartsWith(Path.GetFullPath(linkRoot), resolved);
    }
}
