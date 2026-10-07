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

    [Theory]
    [InlineData("latest", null, "no-promoted-release", "waiting")]
    [InlineData("exact", "1.0.0", "release-available", "passed")]
    [InlineData("exact", "9.0.0", "no-authorized-release", "waiting")]
    public void SelectionReadinessDoesNotFallbackToUnpromotedOrDifferentVersion(string policy, string? version, string code, string state)
    {
        var config = new LauncherConfig { ProjectId = "demo", TargetPlatform = "windows-x64", VersionPolicy = policy, RequestedVersion = version };
        var catalog = new DistributionCatalog { SelectionPolicy = CatalogRecommendation.ExplicitPolicy,
            Projects = [new() { ProjectId = "demo", Releases = [new() { Version = "1.0.0", Platform = "windows-x64", Environment = "prod", Channel = "stable", AllowedClientProfiles = ["general"] }] }] };
        var result = DoctorPresentation.ReleaseReadiness(config, catalog);
        Assert.Equal(code, result.Code); Assert.Equal(state, result.State);
        config.Environment = "dev";
        Assert.Equal("no-authorized-release", DoctorPresentation.ReleaseReadiness(config, catalog).Code);
    }

    [Theory]
    [InlineData(401, "authentication-failed", "admin")]
    [InlineData(403, "access-denied", "admin")]
    [InlineData(503, "server-unavailable", "user")]
    public async Task OnlineFailuresAreActionableAndDoNotChangeInstallation(int status, string code, string owner)
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Root, "config.json");
        await JsonFiles.WriteAsync(path, new LauncherConfig { ProjectId = "demo", InstallDir = "app", StateRootDir = "state", CatalogUrl = "https://localhost:1/catalog", LogDir = "logs" });
        // Exercise the shared transport-error mapping without changing real server state.
        var result = DoctorPresentation.Failure("catalog-online", new HttpRequestException("Authorization: Bearer sentinel", null, (System.Net.HttpStatusCode)status), "client");
        Assert.Equal(code, result.Code); Assert.Equal(owner, result.ActionOwner);
        Assert.DoesNotContain("sentinel", JsonSerializer.Serialize(result));
        var before = Inventory(temp.Root);
        await LauncherDoctor.RunAsync(path, false);
        Assert.Equal(before, Inventory(temp.Root));
    }

    [Fact]
    public void RuntimeDeferredCannotBeReportedAsPassed()
    {
        var check = DoctorPresentation.Normalize(new("runtime-data-host-preflight", false, "At launch"), "agent");
        Assert.Equal("deferred", check.State);
        var report = DoctorPresentation.Complete(new("now", true, "1", "test", [check]));
        Assert.Equal("verification-pending", report.PreparationState);
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
