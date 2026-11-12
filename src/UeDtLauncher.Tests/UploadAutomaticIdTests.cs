using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadAutomaticIdTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "automatic-upload-id-" + Guid.NewGuid().ToString("N"));
    private string Zip()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "payload.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("app.exe").Open()); writer.Write("synthetic only");
        return path;
    }
    private static ReleaseSidecar Metadata(int project, int version, string platform = "windows-x64")
        => new() { ProjectId = new('p', project), Version = new('v', version), Platform = platform, EntryPoint = "app.exe" };

    [Theory]
    [InlineData(1, 1, "windows-x64")]
    [InlineData(128, 1, "windows-x64")]
    [InlineData(1, 128, "windows-x64")]
    [InlineData(128, 128, "windows-x64")]
    [InlineData(128, 128, "linux-x64")]
    public async Task ValidBoundaryReleasesAlwaysGetSegmentSafeDryRunIds(int project, int version, string platform)
    {
        var runner = new Runner();
        var metadata = Metadata(project, version, platform);
        var result = await new ReleaseUploader(runner).ExecuteAsync(Zip(), metadata, new("user@host", "/srv/incoming"), null, Path.Combine(root, "work"), true);
        Assert.True(result.Success); Assert.True(result.DryRun);
        Assert.InRange(result.UploadId.Length, 1, 128);
        ReleaseSidecar.Segment(result.UploadId);
        Assert.Matches(@"\A[A-Za-z0-9][A-Za-z0-9._-]*-\d{17}-[0-9a-f]{16}\z", result.UploadId);
        Assert.Equal(new string('p', project), metadata.ProjectId);
        Assert.Equal(new string('v', version), metadata.Version);
        Assert.EndsWith("/" + result.UploadId, result.RemoteDirectory);
        Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
    }

    [Fact]
    public async Task UploadAlsoAcceptsMaximumLengthReleaseMetadataWithoutAnExplicitId()
    {
        var runner = new Runner();
        var result = await new ReleaseUploader(runner).ExecuteAsync(Zip(), Metadata(128, 128), new("user@host", "/srv/incoming"), null, Path.Combine(root, "work"), false);
        Assert.True(result.Success);
        Assert.Equal(6, runner.Calls);
        Assert.InRange(result.UploadId.Length, 1, 128);
        Assert.False(Directory.Exists(result.WorkDirectory));
    }

    [Fact]
    public void GeneratedRandomSuffixesStayDistinctEvenWithinTheSameClockTick()
    {
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 512; i++)
        {
            var id = ReleaseUploader.AutomaticUploadId(Metadata(128, 128));
            ReleaseSidecar.Segment(id);
            Assert.True(suffixes.Add(id[^16..]));
        }
    }

    [Fact]
    public async Task ExplicitBoundaryIdIsNeverShortenedOrRewritten()
    {
        var id = "Explicit-" + new string('X', 119);
        Assert.Equal(128, id.Length);
        var result = await new ReleaseUploader(new Runner()).ExecuteAsync(Zip(), Metadata(128, 128), new("user@host", "/srv/incoming"), id, root, true);
        Assert.Equal(id, result.UploadId);
    }

    [Theory]
    [InlineData(129, 1)]
    [InlineData(1, 129)]
    public void InvalidReleaseMetadataCannotBeHiddenByPrefixShortening(int project, int version)
        => Assert.Throws<InvalidDataException>(() => ReleaseUploader.AutomaticUploadId(Metadata(project, version)));

    [Fact]
    public async Task OverlongExplicitIdStillFailsBeforeWorkOrTransport()
    {
        var runner = new Runner();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseUploader(runner).ExecuteAsync(Zip(), Metadata(1, 1), new("user@host", "/srv/incoming"), new string('x', 129), Path.Combine(root, "work"), true));
        Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
    }

    private sealed class Runner : IUploadProcessRunner
    {
        public int Calls;
        public bool IsAvailable(string executable) => true;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        { Calls++; return Task.FromResult(0); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
