using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;
public class LauncherEditionTests
{
    [Theory]
    [InlineData(LauncherEdition.General,"developer",false)]
    [InlineData(LauncherEdition.Developer,"general",true)]
    public void ConfigReloadOrMutationCannotChangeCapabilities(LauncherEdition edition,string opposite,bool developer)
    {
        var model=new LauncherDashboardViewModel(edition){Config=new(){ClientProfile=opposite}};
        Assert.Equal(developer,model.IsDeveloper);
        model.Config.ClientProfile=opposite;
        Assert.Equal(developer,model.IsDeveloper);
        model.Config=new(){ClientProfile=opposite};
        Assert.Equal(developer,model.IsDeveloper);
        Assert.Equal(developer?"developer":"general",model.Config.ClientProfile);
    }
}
