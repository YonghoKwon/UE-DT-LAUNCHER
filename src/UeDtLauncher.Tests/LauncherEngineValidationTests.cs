using System.Net;
using System.Net.Sockets;
using Xunit;

namespace UeDtLauncher.Tests;

public class LauncherEngineValidationTests
{
    private static LauncherManifest Manifest(params ManifestFile[] files)
    {
        return new LauncherManifest
        {
            EntryPoint = "game.exe",
            Files = files.ToList()
        };
    }

    private static ManifestFile FileEntry(string path, string? sha256 = null, long size = 1)
    {
        return new ManifestFile { Path = path, Sha256 = sha256 ?? new string('a', 64), Size = size };
    }

    [Fact]
    public void ValidateManifest_AcceptsWellFormedManifest()
    {
        LauncherEngine.ValidateManifest(Manifest(FileEntry("game.exe"), FileEntry("data/content.pak")));
    }

    [Fact]
    public void ValidateManifest_RejectsDuplicatePaths()
    {
        var manifest = Manifest(FileEntry("game.exe"), FileEntry("GAME.EXE"));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifest_RejectsCanonicalDuplicatePaths()
    {
        var manifest = Manifest(FileEntry("game.exe"), FileEntry("data/../game.exe"));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifest_RejectsEntryPointMissingFromFiles()
    {
        var manifest = Manifest(FileEntry("other.exe"));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("not-a-hash")]
    [InlineData("abc123")]
    public void ValidateManifest_RejectsMalformedSha256(string sha)
    {
        var manifest = Manifest(FileEntry("game.exe", sha256: sha));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifest_RejectsNegativeSize()
    {
        var manifest = Manifest(FileEntry("game.exe", size: -1));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Fact]
    public void ValidateManifest_RejectsTraversalPath()
    {
        var manifest = Manifest(FileEntry("game.exe"), FileEntry("../evil.dll"));
        Assert.Throws<InvalidOperationException>(() => LauncherEngine.ValidateManifest(manifest));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void IsTransientDownloadError_ClassifiesHttpStatusCodes(HttpStatusCode statusCode, bool expected)
    {
        var ex = new HttpRequestException("boom", inner: null, statusCode: statusCode);
        Assert.Equal(expected, LauncherEngine.IsTransientDownloadError(ex));
    }

    [Fact]
    public void IsTransientDownloadError_TreatsNetworkLevelErrorsAsTransient()
    {
        Assert.True(LauncherEngine.IsTransientDownloadError(new HttpRequestException("connection reset")));
        Assert.True(LauncherEngine.IsTransientDownloadError(new IOException("disk hiccup")));
        Assert.True(LauncherEngine.IsTransientDownloadError(new SocketException()));
        Assert.True(LauncherEngine.IsTransientDownloadError(new TaskCanceledException()));
    }

    [Fact]
    public void IsTransientDownloadError_TreatsValidationErrorsAsFatal()
    {
        Assert.False(LauncherEngine.IsTransientDownloadError(new InvalidOperationException("bad manifest")));
        Assert.False(LauncherEngine.IsTransientDownloadError(new UnauthorizedAccessException()));
    }
}
