using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(UeDtLauncher.Tests.EnterpriseTestApplication))]
namespace UeDtLauncher.Tests;
public class EnterpriseTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UeDtLauncher.Gui.App>().UseSkia().UseHeadless(new() { UseHeadlessDrawing=false });
}
public class EnterpriseLayoutTests
{
    public static IEnumerable<object[]> Viewports => from dev in new[]{false,true} from pixels in new[]{(1280,720),(1366,768),(1920,1080)} from scale in new[]{1,1.25,1.5}
        select new object[]{dev,pixels.Item1,pixels.Item2,scale};
    private static MainWindow Create(bool developer,double textScale=1)
    {
        var model=new LauncherDashboardViewModel { Config=new LauncherConfig {ClientProfile=developer?"developer":"general",Projects=[new(){ProjectId="demo",DisplayName="포스코DX 디지털 트윈 프로젝트 — 긴 프로젝트 이름 접근성 확인",Description="릴리스 설명"}]},GeneralState=GeneralLauncherState.Ready };
        return new(new("/nonexistent/fixture.json",LauncherConfigSource.Missing,false),model,new(textScale,false),false);
    }
    [AvaloniaTheory][MemberData(nameof(Viewports))]
    public void LayoutKeepsActionsWithinViewport(bool developer,int pixelWidth,int pixelHeight,double scale)
    {
        var width=pixelWidth/scale;var height=pixelHeight/scale;
        var window=Create(developer);window.Show();window.Width=width;window.Height=height;Dispatcher.UIThread.RunJobs();
        try
        {
            var button=window.GetVisualDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)=="primary-action");
            var point=button.TranslatePoint(default,window)!.Value;
            Assert.True(point.X>=0 && point.Y>=0);
            Assert.True(point.X+button.Bounds.Width<=window.ClientSize.Width+1);
            Assert.True(point.Y+button.Bounds.Height<=window.ClientSize.Height+1);
            Assert.All(window.GetLogicalDescendants().OfType<Button>(),b=>Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
        }
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void SettingsDialogHasSafeFocusAndEscapeClosesWithoutApplying()
    {
        var window=Create(true);window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var button=window.GetVisualDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)=="settings");button.Focus();
            var dialog=window.ShowEnterpriseSettings(true);Dispatcher.UIThread.RunJobs();
            var cancel=dialog.GetVisualDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)=="dialog-cancel");
            Assert.True(cancel.IsFocused);Assert.True(cancel.IsCancel);
            Assert.All(dialog.GetVisualDescendants().OfType<Button>(),b=>Assert.False(b.IsDefault));
            dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Assert.False(dialog.IsVisible);Assert.True(button.IsFocused);
        }
        finally {window.Close();}
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void LargeTextAndHighContrastPreservePrimaryAndAutomation(bool developer)
    {
        var model=new LauncherDashboardViewModel {Config=new LauncherConfig {ClientProfile=developer?"developer":"general",Projects=[new(){ProjectId="demo",DisplayName="긴 한글 프로젝트 이름 접근성"}]},GeneralState=GeneralLauncherState.Ready};
        var window=new MainWindow(new("/nonexistent/fixture.json",LauncherConfigSource.Missing,false),model,new(2,true),false);
        window.Show();window.Width=854;window.Height=480;Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(Avalonia.Media.Brushes.Black,window.Background);
            var primary=window.GetLogicalDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)=="primary-action");
            primary.Focus(Avalonia.Input.NavigationMethod.Tab);Dispatcher.UIThread.RunJobs();Assert.Equal(3,primary.BorderThickness.Left);
            var point=primary.TranslatePoint(default,window)!.Value;Assert.True(point.Y+primary.Bounds.Height<=window.ClientSize.Height+1);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(primary)));
        }
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void KeyboardProjectSelectionKeepsFocusOnNewSelection()
    {
        var model=new LauncherDashboardViewModel {Config=new LauncherConfig {ClientProfile="developer",Projects=[new(){ProjectId="a",DisplayName="A"},new(){ProjectId="b",DisplayName="B"}]},GeneralState=GeneralLauncherState.Ready};
        var window=new MainWindow(new("/nonexistent/fixture.json",LauncherConfigSource.Missing,false),model,new(),false);
        window.Show();window.Width=1280;Dispatcher.UIThread.RunJobs();
        try
        {
            window.GetVisualDescendants().OfType<ListBoxItem>().Single(c=>AutomationProperties.GetAutomationId(c)=="project-a").Focus();
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            Assert.Equal("b",model.SelectedProject.ProjectId);
            Assert.Equal("project-b",AutomationProperties.GetAutomationId((Control)window.FocusManager!.GetFocusedElement()!));
        }
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void RuntimeBlockedKeepsDeveloperMutationsDisabled()
    {
        var model=new LauncherDashboardViewModel {Config=new LauncherConfig {ClientProfile="developer",Projects=[new(){ProjectId="demo",DisplayName="demo"}]},GeneralState=GeneralLauncherState.RuntimeBlocked};
        var window=new MainWindow(new("/nonexistent/fixture.json",LauncherConfigSource.Missing,false),model,new(),false);
        window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            foreach(var id in new[]{"primary-action","update","repair","rollback"})
                Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)==id).IsEnabled);
            Assert.True(window.GetLogicalDescendants().OfType<Button>().Single(c=>AutomationProperties.GetAutomationId(c)=="status-check").IsEnabled);
        }
        finally{window.Close();}
    }
    [AvaloniaFact]
    public void InitialExactVersionAttachmentDoesNotStartAnOperation()
    {
        var model=new LauncherDashboardViewModel {Config=new LauncherConfig {ClientProfile="developer",DistributionServerUrl="https://fixture.invalid",VersionPolicy="exact",RequestedVersion="1.0.0",Projects=[new(){ProjectId="demo",DisplayName="demo"}]},GeneralState=GeneralLauncherState.Ready};
        var window=new MainWindow(new("/nonexistent/fixture.json",LauncherConfigSource.Missing,false),model,new(),false);
        window.Show();Dispatcher.UIThread.RunJobs();
        try {Assert.Equal(GeneralLauncherState.Ready,model.GeneralState);Assert.False(model.Running);}
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void ResizingPreservesFocusedSearchText()
    {
        var window=Create(true);window.Show();window.Width=1280;Dispatcher.UIThread.RunJobs();
        try
        {
            var box=window.GetVisualDescendants().OfType<TextBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="project-search");
            box.Text="디지털";box.Focus();box.CaretIndex=2;
            window.Width=1200;Dispatcher.UIThread.RunJobs();
            Assert.Equal("디지털",box.Text);Assert.True(box.IsFocused);Assert.Equal(2,box.CaretIndex);
        }
        finally {window.Close();}
    }
}
