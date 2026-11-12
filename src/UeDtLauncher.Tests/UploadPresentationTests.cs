using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadPresentationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "upload-output-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] Names = ["mkdir", "zip-transfer", "json-transfer", "zip-rename", "json-rename", "verify"];

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TextGoldenShowsCompletedFailedAndUnstartedStages(int failed)
    {
        using var output = new StringWriter();
        var result = new UploadResult(failed < 0, false, "demo-1", "/srv/incoming/demo-1", null,
            failed < 0 ? null : Names[failed], Names.Select(n => new UploadStep(n, "ssh", [])).ToArray(), "확인 필요", "운영자에게 확인");
        UploadPresentation.Write(output, result);
        var expected = new List<string> { failed < 0 ? "업로드 완료" : "업로드 중단", "업로드 ID: demo-1", "원격 폴더: /srv/incoming/demo-1" };
        string[] labels = ["원격 폴더 준비", "ZIP 전송", "설명 파일 전송", "ZIP 최종 이름 변경", "설명 파일 최종 이름 변경", "최종 파일 확인"];
        for (var i = 0; i < Names.Length; i++)
            expected.Add($"{i + 1}. {labels[i]}: " + (failed < 0 || i < failed ? "완료" : i == failed ? "실패" : "미실행"));
        expected.AddRange(["남은 상태: 확인 필요", "다음 할 일: 운영자에게 확인"]);
        Assert.Equal(string.Join(Environment.NewLine, expected) + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task CliDryRunKeepsJsonContractAndSeparatesProgress(string format)
    {
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "payload.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("app.exe").Open())) writer.Write("synthetic");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = await Program.UploadAsync(["--zip", zip, "--project-id", "demo", "--version", "1.0", "--platform", "windows-x64",
            "--entry-point", "app.exe", "--server", "user@host", "--remote-incoming", "/srv/incoming", "--upload-id", "golden", "--dry-run", "--format", format], output, errors);
        Assert.Equal(0, code);
        Assert.Contains("[1/6] ZIP 검사 중", errors.ToString());
        if (format == "json")
        {
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(6, json.RootElement.GetProperty("steps").GetArrayLength());
            Assert.False(output.ToString().Contains("[1/6]", StringComparison.Ordinal));
        }
        else Assert.StartsWith("업로드 계획", output.ToString());
    }

    [Fact]
    public async Task NonNumericPortIsArgumentExitTwoWithoutEchoingInput()
    {
        Assert.Equal(2, await Program.MainAsync(["upload", "--port", "private-invalid-port"]));
        var error = await Assert.ThrowsAsync<CliArgumentException>(() => Program.UploadAsync(["--port", "private-invalid-port"]));
        Assert.DoesNotContain("private-invalid-port", error.Message);
    }

    [Fact]
    public void LargeTransferProgressIsReadableAndSingleLine()
    {
        using var output = new StringWriter();
        UploadPresentation.Progress(output, new(3, "ZIP 전송 중", 3L * 1024 * 1024 * 1024));
        Assert.Equal("[3/6] ZIP 전송 중 (3 GB) — 큰 파일은 오래 걸릴 수 있습니다" + Environment.NewLine, output.ToString());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
