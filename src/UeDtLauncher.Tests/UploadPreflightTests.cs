using System.IO.Compression;
using Xunit;

namespace UeDtLauncher.Tests;

public class UploadPreflightTests
{
    private static UploadProfile Profile => new("upload@host.example", "/srv/incoming", 2222);

    [Fact]
    public async Task CheckUsesOnlyReadOnlyCommandsAndKeepsKeyOnlyStrictAuthentication()
    {
        var runner = new CheckRunner();
        var result = await new UploadPreflight(runner).CheckAsync(Profile);
        Assert.True(result.Success);
        Assert.Equal(new[] { "true", "test -d /srv/incoming", "test -w /srv/incoming" }, runner.Commands.Select(c => c.Arguments.Last()));
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal("ssh", command.Executable);
            Assert.Contains("BatchMode=yes", command.Arguments);
            Assert.Contains("StrictHostKeyChecking=yes", command.Arguments);
            Assert.Contains("UpdateHostKeys=no", command.Arguments);
            Assert.Contains("PasswordAuthentication=no", command.Arguments);
            Assert.Contains("PreferredAuthentications=publickey", command.Arguments);
            Assert.DoesNotContain(command.Arguments, a => a.Contains("mkdir") || a.Contains("mv ") || a.Contains("touch"));
        });
        using var output = new StringWriter();
        UploadPreflight.Write(output, result);
        Assert.Contains("접속·키 인증: 통과", output.ToString());
        Assert.Contains("incoming 쓰기 권한: 통과", output.ToString());
    }

    [Fact]
    public async Task MissingOpenSshStopsBeforeNetworkAndProvidesInstallGuidance()
    {
        var runner = new CheckRunner(available: false);
        var result = await new UploadPreflight(runner).CheckAsync(Profile);
        Assert.False(result.Success);
        Assert.Empty(runner.Commands);
        Assert.All(result.Checks.Take(2), c => Assert.Equal("failed", c.State));
        Assert.All(result.Checks.Skip(2), c => Assert.Equal("not-run", c.State));
        Assert.Contains("OpenSSH 클라이언트", result.Checks[0].NextAction);
    }

    [Theory]
    [InlineData(0, 255, "호스트 키")]
    [InlineData(1, 1, "디렉터리")]
    [InlineData(2, 1, "쓰기 권한")]
    public async Task FailedCheckStopsDependentChecksAndShowsSpecificAction(int failure, int code, string action)
    {
        var runner = new CheckRunner(failure, code);
        var result = await new UploadPreflight(runner).CheckAsync(Profile);
        Assert.False(result.Success);
        Assert.Equal(failure + 1, runner.Commands.Count);
        Assert.Equal("failed", result.Checks[failure + 2].State);
        Assert.Contains(action, result.Checks[failure + 2].NextAction);
        Assert.All(result.Checks.Skip(failure + 3), c => Assert.Equal("not-run", c.State));
    }

    [Theory]
    [InlineData("mkdir", 1, "같은 업로드 ID")]
    [InlineData("zip-transfer", 1, "디스크 부족")]
    [InlineData("json-transfer", 255, "ssh -p 2222 upload@host.example")]
    public void FailureHintsUseOnlyKnownStageAndExitCode(string stage, int code, string expected)
        => Assert.Contains(expected, UploadFailureGuidance.For(stage, code, Profile));

    [Fact]
    public async Task CheckDoesNotCreateLocalFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "preflight-readonly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await new UploadPreflight(new CheckRunner()).CheckAsync(Profile);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public async Task UploadPreservesExit255AndNeverContinuesAfterConnectionFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "upload255-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "payload.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("app.exe").Open())) writer.Write("synthetic");
            var runner = new CheckRunner(0, 255);
            var result = await new ReleaseUploader(runner).ExecuteAsync(zip, new() { ProjectId = "demo", Version = "1.0", EntryPoint = "app.exe" }, Profile, "auth", Path.Combine(root, "work"), false);
            Assert.False(result.Success);
            Assert.Equal(255, result.ExitCode);
            Assert.Single(runner.Commands);
            Assert.Contains("비밀번호를 묻지 않습니다", result.NextAction);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CheckRunner(int failure = -1, int code = 1, bool available = true) : IUploadProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Commands { get; } = [];
        public bool IsAvailable(string executable) => available;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        {
            Commands.Add((executable, arguments));
            return Task.FromResult(Commands.Count - 1 == failure ? code : 0);
        }
    }
}
