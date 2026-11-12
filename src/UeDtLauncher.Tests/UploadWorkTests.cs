using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadWorkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "upload-work-tests-" + Guid.NewGuid().ToString("N"));
    private string WorkRoot => Path.Combine(root, "work");
    private string Zip()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "input.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("app.exe").Open());
        writer.Write("synthetic only");
        return path;
    }
    private static ReleaseSidecar Metadata => new() { ProjectId = "demo", Version = "1.0", EntryPoint = "app.exe" };
    private static UploadProfile Profile => new("user@host", "/srv/incoming");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulUploadDeletesOnlyItsWorkUnlessKeepWasRequested(bool keep)
    {
        var zip = Zip();
        var sibling = Path.Combine(WorkRoot, "previous"); Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "user-data"), "preserve");
        var result = await new ReleaseUploader(new Runner()).ExecuteAsync(zip, Metadata, Profile, "new", WorkRoot, false, keepWork: keep);
        Assert.True(result.Success);
        Assert.Equal(keep, result.WorkPreserved);
        Assert.Equal(keep, Directory.Exists(result.WorkDirectory));
        Assert.True(File.Exists(zip));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(sibling, "user-data")));
    }

    [Fact]
    public async Task FailedUploadPreservesWorkAndPointsToIt()
    {
        var result = await new ReleaseUploader(new Runner(fail: true)).ExecuteAsync(Zip(), Metadata, Profile, "failed", WorkRoot, false);
        Assert.False(result.Success);
        Assert.True(result.WorkPreserved);
        Assert.True(File.Exists(Path.Combine(result.WorkDirectory!, "payload.zip")));
        using var output = new StringWriter(); UploadPresentation.Write(output, result);
        Assert.Contains(DiagnosticRedactor.Redact(result.WorkDirectory), output.ToString());
        Assert.Contains(Path.GetFileName(result.WorkDirectory!), output.ToString());
    }

    [Fact]
    public async Task InsufficientSpaceNeverCreatesWorkOrCallsTransport()
    {
        var runner = new Runner();
        var result = await new ReleaseUploader(runner, _ => 0).ExecuteAsync(Zip(), Metadata, Profile, "full", WorkRoot, false);
        Assert.False(result.Success);
        Assert.Equal("prepare", result.FailedStage);
        Assert.Equal(0, runner.Calls);
        Assert.False(Directory.Exists(WorkRoot));
        Assert.Null(result.WorkDirectory);
        Assert.Equal(6, result.Steps.Count);
        using var output = new StringWriter(); UploadPresentation.Write(output, result);
        foreach (var step in result.Steps) Assert.Contains(UploadPresentation.Stage(step.Name) + ": 미실행", output.ToString());
    }

    [Fact]
    public async Task OriginalChangeBeforeCopyIsRejectedAndPreservesEvidence()
    {
        var zip = Zip();
        var runner = new Runner();
        var changed = false;
        var result = await new ReleaseUploader(runner).ExecuteAsync(zip, Metadata, Profile, "changed", WorkRoot, false,
            progress: value =>
            {
                if (value.Message != "로컬 작업 사본 준비 중" || changed) return;
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
                archive.GetEntry("app.exe")!.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("app.exe").Open()); writer.Write("different synthetic contents");
                changed = true;
            });
        Assert.True(changed);
        Assert.False(result.Success);
        Assert.Equal("integrity-failed", result.FailureKind);
        Assert.True(result.WorkPreserved);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void CleanupCannotTargetTheRootOrAnOutsideDirectory()
    {
        Directory.CreateDirectory(WorkRoot);
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "payload.zip"), "preserve");
        Assert.ThrowsAny<Exception>(() => UploadWork.DeleteOwned(WorkRoot, outside));
        Assert.ThrowsAny<Exception>(() => UploadWork.DeleteOwned(WorkRoot, WorkRoot));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(outside, "payload.zip")));
    }

    [Fact]
    public async Task LateUnownedContentIsPreservedWithoutReclassifyingRemoteSuccessAsFailure()
    {
        var runner = new Runner(addUnexpected: true);
        var result = await new ReleaseUploader(runner).ExecuteAsync(Zip(), Metadata, Profile, "late", WorkRoot, false);
        Assert.True(result.Success);
        Assert.True(result.WorkPreserved);
        Assert.NotNull(result.WorkCleanupWarning);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(result.WorkDirectory!, "user-data")));
    }

    private sealed class Runner(bool fail = false, bool addUnexpected = false) : IUploadProcessRunner
    {
        public int Calls;
        private string? work;
        public bool IsAvailable(string executable) => true;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls++;
            if (executable == "scp") work = Path.GetDirectoryName(arguments[^2]);
            if (addUnexpected && Calls == 6) File.WriteAllText(Path.Combine(work!, "user-data"), "preserve");
            return Task.FromResult(fail ? 1 : 0);
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
