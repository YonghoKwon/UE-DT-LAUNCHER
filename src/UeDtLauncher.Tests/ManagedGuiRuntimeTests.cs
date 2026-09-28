using UeDtLauncher.Gui;
using Xunit;
namespace UeDtLauncher.Tests;

public class ManagedGuiRuntimeTests
{
    [Theory][InlineData(RuntimeState.Running)][InlineData(RuntimeState.LaunchPending)][InlineData(RuntimeState.Unknown)]
    public void ActiveRuntimeCannotBecomeReadyOrOfferRollback(RuntimeState state)
    {
        var vm=new LauncherDashboardViewModel { GeneralState=GeneralLauncherState.Ready };
        var observation=new RuntimeObservation(state,"blocked","실행 상태 확인 필요");
        Assert.False(vm.ApplyRuntimeObservation(observation));
        Assert.Equal(PrimaryActionKind.Disabled,vm.PrimaryAction);
        var error=Assert.Throws<RuntimeBlockedException>(()=>vm.RequireRuntimeQuiescent(observation));
        Assert.False(LauncherDashboardViewModel.CanOfferRecoveryRollback(true,true,error));
        Assert.False(LauncherDashboardViewModel.CanOfferRecoveryRollback(true,true,new Exception("outer",error)));
    }
    [Fact]
    public void MissingObservationBlocksRatherThanInventingCompletion()
    {
        var vm=new LauncherDashboardViewModel { GeneralState=GeneralLauncherState.Ready };
        var error=Assert.Throws<RuntimeBlockedException>(()=>vm.RequireRuntimeQuiescent(null));
        Assert.Equal(RuntimeState.Unknown,error.Observation.State);
        Assert.Equal(PrimaryActionKind.Disabled,vm.PrimaryAction);
    }
    [Fact]
    public void QuiescentPreservesInstallStatusAndRollbackRequiresRepairFailure()
    {
        var vm=new LauncherDashboardViewModel { GeneralState=GeneralLauncherState.NotInstalled };
        Assert.True(vm.ApplyRuntimeObservation(new(RuntimeState.Quiescent,"new-install","")));
        Assert.Equal(PrimaryActionKind.InstallAndLaunch,vm.PrimaryAction);
        Assert.False(LauncherDashboardViewModel.CanOfferRecoveryRollback(false,true,new IOException()));
        Assert.False(LauncherDashboardViewModel.CanOfferRecoveryRollback(true,false,new IOException()));
        Assert.True(LauncherDashboardViewModel.CanOfferRecoveryRollback(true,true,new IOException()));
    }
}
