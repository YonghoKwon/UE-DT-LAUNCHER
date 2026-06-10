using Xunit;

namespace UeDtLauncher.Tests;

public class SafePathTests
{
    [Theory]
    [InlineData("file.txt")]
    [InlineData("sub/dir/file.txt")]
    [InlineData("sub\\dir\\file.txt")]
    [InlineData("Windows/m7at10_dt.exe")]
    public void ResolveInside_AllowsRelativePathsUnderRoot(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "safepath-root");
        var resolved = SafePath.ResolveInside(root, relative);
        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..")]
    public void ResolveInside_RejectsTraversal(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "safepath-root");
        Assert.Throws<InvalidOperationException>(() => SafePath.ResolveInside(root, relative));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveInside_RejectsEmptyPaths(string relative)
    {
        Assert.Throws<InvalidOperationException>(() => SafePath.ResolveInside("root", relative));
    }

    [Fact]
    public void ResolveInside_RejectsRootedPaths()
    {
        var rooted = OperatingSystem.IsWindows() ? "C:\\evil.txt" : "/etc/passwd";
        Assert.Throws<InvalidOperationException>(() => SafePath.ResolveInside("root", rooted));
    }
}
