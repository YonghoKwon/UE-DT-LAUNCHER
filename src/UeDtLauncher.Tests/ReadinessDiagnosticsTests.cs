using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ReadinessDiagnosticsTests
{
    [Fact]
    public async Task OfflineDoctorDoesNotMigrateLegacyStateOrCreateDirectories()
    {
        using var temp = new TestDirectory();
        var config = new LauncherConfig { ProjectId = "demo", InstallDir = "app", StateRootDir = "state", LogDir = "logs" };
        var path = Path.Combine(temp.Root, "config.json");
        await JsonFiles.WriteAsync(path, config);
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "installed-manifest.json"), "preserve legacy manifest");
        var before = Inventory(temp.Root);
        var report = await LauncherDoctor.RunAsync(path, false);
        Assert.Equal(before, Inventory(temp.Root));
        Assert.Equal("verification-pending", report.PreparationState);
        Assert.Contains(report.Checks, c => c.Name == "agent" && c.State == "not-applicable");
        Assert.DoesNotContain(report.Checks, c => c.Code == "service-unavailable");
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "state")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"security\":null}")]
    public async Task InvalidConfigurationIsSafeAndReadOnly(string json)
    {
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Root, "config.json");
        await File.WriteAllTextAsync(path, json);
        var before = Inventory(temp.Root);
        var report = await LauncherDoctor.RunAsync(path, false);
        Assert.False(report.Healthy); Assert.Equal("action-required", report.PreparationState);
        Assert.Equal(before, Inventory(temp.Root));
        Assert.DoesNotContain(path, JsonSerializer.Serialize(report));
        Assert.NotNull(report.SupportId);
    }

    [Fact]
    public void OldReportDoesNotBecomeReadyAndWaitingIsNotTechnicalFailure()
    {
        var old = JsonSerializer.Deserialize<DoctorReport>("{\"generatedUtc\":\"now\",\"healthy\":true,\"launcherVersion\":\"1\",\"operatingSystem\":\"test\",\"checks\":[]}", JsonFiles.Options)!;
        Assert.Null(old.PreparationState); Assert.Equal("추가 검증 필요", DoctorPresentation.Summary(old));
        var waiting = DoctorPresentation.Complete(old with { Checks = [new("recommendation", false, "승격 대기") { State = "waiting" }] });
        Assert.True(waiting.Healthy); Assert.Equal("action-required", waiting.PreparationState);
    }

    [Fact]
    public void DiagnosticTargetRejectsPathAndModeClaims()
    {
        var config = new LauncherConfig { ProjectId = "demo" };
        var target = DoctorTarget.From(config);
        Assert.Throws<InvalidDataException>(() => (target with { ProjectId = "../outside" }).Apply(config));
        Assert.Throws<ArgumentException>(() => (target with { DeploymentMode = "managed-agent" }).Apply(config));
        Assert.Throws<InvalidDataException>(() => (target with { VersionPolicy = "exact", RequestedVersion = null }).Apply(config));
    }

    private static string Inventory(string root) => string.Join('\n', Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(root, p) + (File.Exists(p) ? ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "/")));
    private sealed class TestDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-readiness-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
