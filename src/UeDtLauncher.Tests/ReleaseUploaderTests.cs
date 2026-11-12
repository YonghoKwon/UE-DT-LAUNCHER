using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public class ReleaseUploaderTests
{
    private static ReleaseSidecar Metadata() => new() { ProjectId = "demo", Version = "1.0", Platform = "windows-x64", EntryPoint = "app.exe" };
    private static UploadProfile Profile() => new("upload@host.example", "/srv/incoming", 2222);

    private static string Zip(string directory, string entry = "app.exe")
    {
        var path = Path.Combine(directory, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var output = new StreamWriter(archive.CreateEntry(entry).Open());
        output.Write("synthetic payload");
        return path;
    }

    [Fact]
    public async Task SuccessfulUploadUsesFreshSnapshotAndOrderedPublication()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner();
        var result = await new ReleaseUploader(runner).ExecuteAsync(Zip(temp.Path), Metadata(), Profile(), "upload-1", Path.Combine(temp.Path, "work"), false);
        Assert.True(result.Success);
        Assert.Equal(new[] { "ssh", "scp", "scp", "ssh", "ssh", "ssh" }, runner.Calls.Select(c => c.Executable));
        Assert.Contains("mkdir -- /srv/incoming/upload-1", runner.Calls[0].Arguments);
        Assert.Contains(runner.Calls[1].Arguments, a => a.EndsWith("payload.zip.uploading"));
        Assert.Contains(runner.Calls[2].Arguments, a => a.EndsWith("release.json.uploading"));
        Assert.StartsWith("mv -n --", runner.Calls[3].Arguments.Last());
        Assert.StartsWith("mv -n --", runner.Calls[4].Arguments.Last());
        var metadata = await SidecarPackageValidator.ReadAsync(Path.Combine(result.WorkDirectory!, "release.json"));
        Assert.Equal("payload.zip", metadata.PackageFile);
        Assert.Equal(await Hashing.Sha256FileAsync(Path.Combine(result.WorkDirectory!, "payload.zip")), metadata.PackageSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AnyFailureStopsLaterStepsAndTransferFailureNeverRenames(int stage)
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner(stage);
        var result = await new ReleaseUploader(runner).ExecuteAsync(Zip(temp.Path), Metadata(), Profile(), "upload-1", Path.Combine(temp.Path, "work"), false);
        Assert.False(result.Success);
        Assert.Equal(stage + 1, runner.Calls.Count);
        Assert.Equal(result.Steps[stage].Name, result.FailedStage);
        if (stage < 3) Assert.DoesNotContain(runner.Calls, call => call.Arguments.Any(a => a.StartsWith("mv ")));
        Assert.NotEmpty(result.Remaining);
        Assert.NotEmpty(result.NextAction);
    }

    [Theory]
    [InlineData("upload@host;id", "/srv/incoming", "valid")]
    [InlineData("-option@host", "/srv/incoming", "valid")]
    [InlineData("upload@host", "/srv/../incoming", "valid")]
    [InlineData("upload@host", "/srv/incoming$(id)", "valid")]
    [InlineData("upload@host", "/srv/incoming", "bad;id")]
    public async Task InjectionIsRejectedBeforeNetworkOrLocalWork(string host, string remote, string id)
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner();
        var work = Path.Combine(temp.Path, "work");
        await Assert.ThrowsAnyAsync<Exception>(() => new ReleaseUploader(runner).ExecuteAsync(Zip(temp.Path), Metadata(), new(host, remote), id, work, false));
        Assert.Empty(runner.Calls);
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public async Task DryRunDoesNotCreateFilesOrCallNetwork()
    {
        using var temp = new TempDirectory();
        var zip = Zip(temp.Path);
        var before = Directory.GetFileSystemEntries(temp.Path).Order().ToArray();
        var runner = new FakeRunner();
        var result = await new ReleaseUploader(runner).ExecuteAsync(zip, Metadata(), Profile(), "dry", Path.Combine(temp.Path, "work"), true);
        Assert.True(result.DryRun);
        Assert.True(result.Success);
        Assert.Equal(before, Directory.GetFileSystemEntries(temp.Path).Order().ToArray());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UnsafeZipIsRejectedWithoutRemoteDirectory()
    {
        using var temp = new TempDirectory();
        var runner = new FakeRunner();
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseUploader(runner).ExecuteAsync(Zip(temp.Path, "../app.exe"), Metadata(), Profile(), "badzip", temp.Path, true));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ProfileIsExclusiveAndCommandValuesOverrideOnlySuppliedFields()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "upload.profile.json");
        await UploadProfile.InitializeAsync(path, false);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => UploadProfile.InitializeAsync(path, false));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var merged = Profile().Override(new("other@host", Port: 2223));
        Assert.Equal("other@host", merged.Server);
        Assert.Equal(2223, merged.Port);
        Assert.Equal("/srv/incoming", merged.RemoteIncoming);
    }

    private sealed class FakeRunner(int failure = -1) : IUploadProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(Calls.Count - 1 == failure ? 1 : 0);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "upload-tests-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
