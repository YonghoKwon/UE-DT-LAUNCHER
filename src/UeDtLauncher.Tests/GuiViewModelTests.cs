using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class GuiViewModelTests
{
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
}
