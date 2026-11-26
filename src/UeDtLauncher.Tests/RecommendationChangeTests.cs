using System.Text;
using System.Text.Json;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class RecommendationChangeTests
{
    [Theory]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("2.0.0", "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0", "v1.0.0", false)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("build-1", "build-2", false)]
    public void RequiresALowerComparableRecommendation(string version, string? previous, bool expected)
    {
        var hint = previous is null ? null : new PreviousInstallation(Selection with { Version = previous }, "2026-11-26T00:00:00Z");
        Assert.Equal(expected, RecommendationChange.Find("latest", Catalog(version), Status(version, hint)) is not null);
    }

    [Theory]
    [InlineData("another", "prod", "stable", "windows-x64")]
    [InlineData("demo", "dev", "stable", "windows-x64")]
    [InlineData("demo", "prod", "beta", "windows-x64")]
    [InlineData("demo", "prod", "stable", "linux-x64")]
    public void IgnoresHintsForOtherTracksAndPlatforms(string project, string environment, string channel, string platform)
    {
        var hint = new PreviousInstallation(new(project, environment, channel, platform, "2.0.0"), "2026-11-26T00:00:00Z");
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0"), Status("1.0.0", hint)));
    }

    [Fact]
    public void RequiresCatalogRecommendationAndMatchingStatus()
    {
        var hint = new PreviousInstallation(Selection, "2026-11-26T00:00:00Z");
        Assert.Null(RecommendationChange.Find("latest", null, Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0", latest: false), Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("exact", Catalog("1.0.0"), Status("1.0.0", hint)));
        Assert.Null(RecommendationChange.Find("latest", Catalog("1.0.0"), Status("1.1.0", hint)));
    }

    [Theory]
    [InlineData(true, "1.0.0", "2.0.0", true)]
    [InlineData(false, "1.0.0", "2.0.0", true)]
    [InlineData(true, "2.0.0", "2.0.0", false)]
    [InlineData(true, "3.0.0", "2.0.0", false)]
    [InlineData(true, "1.0.0", "1.0.0", false)]
    public async Task UsesLastActivationAcrossRetainedInstallations(
        bool retainV1, string recommended, string lastActive, bool expected)
    {
        using var fixture = new InstallationFixture();
        var v1 = Selection with { Version = "1.0.0" };
        if (retainV1) await fixture.InstallAsync(v1, "2026-11-26T00:00:00Z");
        await fixture.InstallAsync(Selection, "2026-11-26T01:00:00Z");
        if (lastActive == v1.Version)
            fixture.RecordSupervisedRun(v1);

        var selected = Selection with { Version = recommended };
        VersionedReleasePaths.Bind(fixture.Config, selected);
        var status = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(selected));
        // Exercise the IPC wire shape, including older agents' absent-field behavior in the existing tests.
        status = JsonSerializer.Deserialize<ManagedProjectStatus>(JsonSerializer.Serialize(status, JsonFiles.Options),
            JsonFiles.Options)!;

        var installed = recommended == Selection.Version || (retainV1 && recommended == v1.Version);
        Assert.Equal(installed, status.IsInstalled);
        Assert.Equal(installed ? recommended : null, status.InstalledVersion);
        if (installed) Assert.Null(status.PreviousInstallation);
        Assert.Equal(lastActive, status.RecommendationHistory?.LastActiveRelease?.Version);
        Assert.Equal(expected, RecommendationChange.Find("latest", Catalog(recommended), status) is not null);
        Assert.Equal(selected, fixture.Config.SelectedRelease);
    }

    [Fact]
    public async Task ExcludesDeveloperOnlyInstallationFromGeneralRecommendation()
    {
        using var fixture = new InstallationFixture();
        var v1 = Selection with { Version = "1.0.0" };
        await fixture.InstallAsync(Selection, "2026-11-26T01:00:00Z");
        fixture.Config.AuthenticatedCatalog!.Projects.Single().Releases.Single(release => release.Version == Selection.Version)
            .AllowedClientProfiles = ["developer"];
        VersionedReleasePaths.Bind(fixture.Config, v1);
        var status = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(v1));
        Assert.Equal(Selection, status.PreviousInstallation?.Release);
        Assert.Null(Assert.IsType<RecommendationHistory>(status.RecommendationHistory).LastActiveRelease);
        Assert.Null(RecommendationChange.Find("latest", Catalog(v1.Version), status));
    }

    [Theory]
    [InlineData("general")]
    [InlineData("developer")]
    public async Task DamagedCandidateDoesNotHideRunningRelease(string profile)
    {
        using var fixture = new InstallationFixture();
        var v1 = Selection with { Version = "1.0.0" };
        var v3 = Selection with { Version = "3.0.0" };
        await fixture.InstallAsync(v1, "2026-11-26T00:00:00Z");
        await fixture.InstallAsync(Selection, "2026-11-26T01:00:00Z");
        await fixture.InstallAsync(v3, "2026-11-26T03:00:00Z");
        fixture.Config.AuthenticatedCatalog!.Projects.Single().Releases.Single(release => release.Version == v3.Version)
            .AllowedClientProfiles = [profile];
        await File.WriteAllTextAsync(fixture.Config.InstallStatePath, "{");
        fixture.RecordSupervisedRun(Selection, completed: false);
        Assert.Equal(RuntimeState.Running, RuntimeStore.Observe(fixture.Config).State);

        VersionedReleasePaths.Bind(fixture.Config, v1);
        var status = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(v1));
        Assert.Equal(Selection, RecommendationChange.Find("latest", Catalog(v1.Version), status)?.Previous);
    }

    [Fact]
    public async Task AbortedLaunchAfterSuccessfulRunDoesNotReviveAnOlderActivation()
    {
        using var fixture = new InstallationFixture();
        var v1 = Selection with { Version = "1.0.0" };
        await fixture.InstallAsync(v1, "2026-11-26T00:00:00Z");
        await fixture.InstallAsync(Selection, "2026-11-26T01:00:00Z");
        fixture.RecordSupervisedRun(v1);
        var beforeAbort = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(v1));
        Assert.Equal(v1, beforeAbort.RecommendationHistory?.LastActiveRelease);

        var identity = RuntimeIdentities.Current();
        var ticket = RuntimeStore.Begin(fixture.Config, identity, identity.Executable);
        RuntimeStore.AbortBeforeStart(fixture.Config, ticket, identity);
        Assert.Equal("launch-aborted", RuntimeStore.Read(fixture.Config)?.Origin);

        var status = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(v1));
        Assert.Null(Assert.IsType<RecommendationHistory>(status.RecommendationHistory).LastActiveRelease);
        Assert.Null(RecommendationChange.Find("latest", Catalog(v1.Version), status));

        fixture.RecordSupervisedRun(Selection, completed: false);
        VersionedReleasePaths.Bind(fixture.Config, v1);
        status = await ManagedProjectStatusInspector.InspectAsync(fixture.Config, fixture.Manifest(v1));
        Assert.Equal(Selection, RecommendationChange.Find("latest", Catalog(v1.Version), status)?.Previous);
    }

    private static readonly ReleaseSelection Selection = new("demo", "prod", "stable", "windows-x64", "2.0.0");

    private static CatalogReleaseOption Catalog(string version, bool latest = true) => new()
    {
        ProjectId = Selection.ProjectId, Environment = Selection.Environment, Channel = Selection.Channel,
        Platform = Selection.Platform, Version = version, IsLatest = latest
    };

    private static ManagedProjectStatus Status(string version, PreviousInstallation? hint) =>
        new(false, null, version, true, 0, 0, false, hint);

    private sealed class InstallationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uedt-recall-" + Guid.NewGuid().ToString("N"));
        private static readonly byte[] Payload = Encoding.ASCII.GetBytes("test");
        private static readonly string Digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Payload)).ToLowerInvariant();
        internal LauncherConfig Config { get; }

        internal InstallationFixture()
        {
            Config = new()
            {
                StateRootDir = Path.Combine(_root, "state"), VersionedInstallRoot = Path.Combine(_root, "apps"),
                CatalogAuthenticated = true,
                AuthenticatedCatalog = new()
                {
                    Projects = [new() { ProjectId = Selection.ProjectId, Releases = new[] { "1.0.0", "2.0.0", "3.0.0" }
                        .Select(version => new DistributionRelease { Version = version, Platform = Selection.Platform }).ToList() }]
                }
            };
        }

        internal LauncherManifest Manifest(ReleaseSelection selection) => new()
        {
            AppId = selection.ProjectId, Version = selection.Version, Platform = selection.Platform,
            Channel = selection.Channel, EntryPoint = "game.exe",
            Files = [new() { Path = "game.exe", Size = Payload.Length, Sha256 = Digest }]
        };

        internal async Task InstallAsync(ReleaseSelection selection, string time)
        {
            VersionedReleasePaths.Bind(Config, selection);
            Directory.CreateDirectory(Config.InstallDir);
            Directory.CreateDirectory(Path.GetDirectoryName(Config.InstallStatePath)!);
            await File.WriteAllBytesAsync(Path.Combine(Config.InstallDir, "game.exe"), Payload);
            await JsonFiles.WriteAsync(Config.InstalledManifestPath, Manifest(selection));
            await JsonFiles.WriteAsync(Config.InstallStatePath, new InstallState
            {
                Version = selection.Version, Environment = selection.Environment, Channel = selection.Channel,
                Platform = selection.Platform, InstalledAtUtc = time,
                ManifestSha256 = await Hashing.Sha256FileAsync(Config.InstalledManifestPath)
            });
            RuntimeStore.Initialize(Config);
        }

        internal void RecordSupervisedRun(ReleaseSelection selection, bool completed = true)
        {
            VersionedReleasePaths.Bind(Config, selection);
            var identity = RuntimeIdentities.Current();
            var ticket = RuntimeStore.Begin(Config, identity, identity.Executable);
            RuntimeStore.Attach(Config, ticket, identity, identity.Executable);
            RuntimeStore.Report(Config, ticket, identity, completed: false);
            if (completed) RuntimeStore.Report(Config, ticket, identity, completed: true);
            File.SetLastWriteTimeUtc(RuntimeStore.RecordPath(Config), new DateTime(2026, 11, 26, 2, 0, 0, DateTimeKind.Utc));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
