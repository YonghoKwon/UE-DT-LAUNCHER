using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class GuiViewModelTests
{
    [Fact]
    public void StartupOptions_ExplicitConfigWinsEvenWhenMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-startup-explicit", Guid.NewGuid().ToString("N"));
        var managed = LayoutUnder(Path.Combine(root, "managed"));
        Directory.CreateDirectory(managed.ConfigRoot);
        File.WriteAllText(Path.Combine(managed.ConfigRoot, "launcher.config.json"), "{}");

        var options = LauncherStartupOptions.Discover(
            ["--config", "custom.json"], managed, Path.Combine(root, "portable"), root);

        Assert.Equal(LauncherConfigSource.Explicit, options.ConfigSource);
        Assert.Equal(Path.Combine(root, "custom.json"), options.ConfigPath);
        Assert.False(options.ConfigExists);
    }

    [Fact]
    public void StartupOptions_UsesManagedBeforePortableConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-startup-managed", Guid.NewGuid().ToString("N"));
        var managed = LayoutUnder(Path.Combine(root, "managed"));
        var portable = Path.Combine(root, "portable");
        Directory.CreateDirectory(managed.ConfigRoot);
        Directory.CreateDirectory(portable);
        File.WriteAllText(Path.Combine(managed.ConfigRoot, "launcher.config.json"), "{}");
        File.WriteAllText(Path.Combine(portable, "launcher.config.json"), "{}");

        var options = LauncherStartupOptions.Discover([], managed, portable, root);

        Assert.Equal(LauncherConfigSource.Managed, options.ConfigSource);
        Assert.Equal(Path.Combine(managed.ConfigRoot, "launcher.config.json"), options.ConfigPath);
        Assert.True(options.ConfigExists);
    }

    [Fact]
    public void StartupOptions_FallsBackToPortableOrFriendlyMissingState()
    {
        var root = Path.Combine(Path.GetTempPath(), "launcher-startup-portable", Guid.NewGuid().ToString("N"));
        var managed = LayoutUnder(Path.Combine(root, "managed"));
        var portable = Path.Combine(root, "portable");
        Directory.CreateDirectory(portable);
        var portableConfig = Path.Combine(portable, "launcher.config.json");
        File.WriteAllText(portableConfig, "{}");

        var found = LauncherStartupOptions.Discover([], managed, portable, root);
        File.Delete(portableConfig);
        var missing = LauncherStartupOptions.Discover([], managed, portable, root);

        Assert.Equal(LauncherConfigSource.Portable, found.ConfigSource);
        Assert.True(found.ConfigExists);
        Assert.Equal(LauncherConfigSource.Missing, missing.ConfigSource);
        Assert.False(missing.ConfigExists);
        Assert.Equal(portableConfig, missing.ConfigPath);
    }

    [Fact]
    public void GeneralProfile_CannotReachDeveloperCapabilitiesOrTechnicalErrors()
    {
        var model = new LauncherDashboardViewModel
        {
            Config = new LauncherConfig { ClientProfile = "general" }
        };

        Assert.False(model.Capabilities.CanRepair);
        Assert.False(model.Capabilities.CanChangeReleaseTrack);
        Assert.False(model.Capabilities.CanViewTechnicalErrors);
        var friendly = model.FriendlyError(new InvalidOperationException("Bearer super-secret signature failed"));
        Assert.DoesNotContain("super-secret", friendly);
        Assert.DoesNotContain("Bearer", friendly);
        Assert.Contains("보안 검증", friendly);
    }

    [Fact]
    public void DeveloperProfile_ExposesCapabilitiesAndSanitizedTechnicalError()
    {
        var model = new LauncherDashboardViewModel
        {
            Config = new LauncherConfig { ClientProfile = "developer" }
        };

        Assert.True(model.Capabilities.CanRepair);
        Assert.True(model.Capabilities.CanChangeReleaseTrack);
        Assert.True(model.Capabilities.CanViewTechnicalErrors);
        var error = model.FriendlyError(new InvalidOperationException("Authorization: Bearer dev-secret failed"));
        Assert.Contains("<redacted>", error);
        Assert.DoesNotContain("dev-secret", error);
    }

    [Fact]
    public void VisibleProjects_AppliesProfileSearchPinAndSortPolicy()
    {
        var model = new LauncherDashboardViewModel
        {
            Config = new LauncherConfig
            {
                ClientProfile = "general",
                Projects =
                {
                    new ProjectUiConfig { ProjectId = "b", DisplayName = "Beta", SortOrder = 2, VisibleToProfiles = { "general" } },
                    new ProjectUiConfig { ProjectId = "a", DisplayName = "Alpha", SortOrder = 9, IsPinned = true, VisibleToProfiles = { "general" } },
                    new ProjectUiConfig { ProjectId = "dev", DisplayName = "Developer", VisibleToProfiles = { "developer" } }
                }
            }
        };

        Assert.Equal(new[] { "a", "b" }, model.VisibleProjects().Select(project => project.ProjectId));
        model.Search = "beta";
        Assert.Equal("b", Assert.Single(model.VisibleProjects()).ProjectId);
    }

    private static ManagedLauncherPathLayout LayoutUnder(string root) => new(
        Path.Combine(root, "install"),
        Path.Combine(root, "config"),
        Path.Combine(root, "state"),
        Path.Combine(root, "apps"),
        Path.Combine(root, "logs"),
        Path.Combine(root, "credentials"));
}
