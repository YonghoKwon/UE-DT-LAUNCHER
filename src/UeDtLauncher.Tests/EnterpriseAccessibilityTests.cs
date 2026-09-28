using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

// These are control/keyboard tests in DIP, not Windows DPI or Narrator evidence.
public class EnterpriseAccessibilityTests
{
    private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;

    private static MainWindow Create(bool developer, double scale=2, bool contrast=false,
        GeneralLauncherState state=GeneralLauncherState.Ready)
    {
        var model=new LauncherDashboardViewModel
        {
            Config=new LauncherConfig
            {
                ClientProfile=developer?"developer":"general",
                Projects=[new(){ProjectId="demo",DisplayName="포스코DX 디지털 트윈 긴 프로젝트 이름",Description="긴 릴리스 설명과 지원 안내"}]
            },
            GeneralState=state,
            Running=state==GeneralLauncherState.Working,
            InstallState=state==GeneralLauncherState.RuntimeBlocked?"실행 상태 확인 필요":"설치 상태",
            InstallDetail=string.Concat(Enumerable.Repeat("선택한 배포와 설치 상태를 확인해 주세요. ",8))
        };
        return new(new("/nonexistent/accessibility-fixture.json",LauncherConfigSource.Missing,false),model,new(scale,contrast),false);
    }

    private static T Find<T>(Window window,string id) where T:Control => window.GetVisualDescendants()
        .OfType<T>().Single(c=>AutomationProperties.GetAutomationId(c)==id);
    private static void Invoke(MainWindow window,string method) => typeof(MainWindow).GetMethod(method,PrivateInstance)!.Invoke(window,null);
    private static LauncherOperationPresentation Presentation(MainWindow window) =>
        (LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation",PrivateInstance)!.GetValue(window)!;
    private static void SetPreferences(MainWindow window,LauncherUiPreferences preferences) =>
        typeof(MainWindow).GetField("_preferences",PrivateInstance)!.SetValue(window,preferences);

    private static void WithinWindow(Window window,Control control)
    {
        var point=control.TranslatePoint(default,window)!.Value;
        var id=AutomationProperties.GetAutomationId(control)??control.GetType().Name;
        Assert.True(control.Bounds.Width>0 && control.Bounds.Height>0,$"{id} must have a usable size.");
        Assert.True(point.X>=-1 && point.Y>=-1,$"{id} starts outside the window: {point}.");
        Assert.True(point.X+control.Bounds.Width<=window.ClientSize.Width+1,$"{id} exceeds window width.");
        Assert.True(point.Y+control.Bounds.Height<=window.ClientSize.Height+1,$"{id} exceeds window height: {point}, {control.Bounds}, {window.ClientSize}.");
    }

    [AvaloniaTheory]
    [InlineData(false,1)][InlineData(false,2)][InlineData(true,1)][InlineData(true,2)]
    public void ExpanderAndTabHeadersUseTheSelectedTextScale(bool developer,double scale)
    {
        var window=Create(developer,scale);window.Show();window.Width=1000;Dispatcher.UIThread.RunJobs();
        try
        {
            var expanders=window.GetVisualDescendants().OfType<Expander>().ToList();
            Assert.NotEmpty(expanders);
            foreach(var expander in expanders)
            {
                var header=expander.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==expander.Header as string);
                Assert.Equal(LauncherVisualTokens.FontBody*scale,header.FontSize);
            }
            if(developer)
            {
                var tabs=window.GetVisualDescendants().OfType<TabItem>().ToList();
                Assert.Equal(2,tabs.Count);
                foreach(var tab in tabs)
                {
                    var header=tab.GetVisualDescendants().OfType<TextBlock>().First(t=>t.Text==tab.Header as string);
                    Assert.Equal(LauncherVisualTokens.FontBody*scale,header.FontSize);
                }
            }
        }
        finally{window.Close();}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void FocusedDialogButtonRetainsContrastingBorderAcrossContrastChanges(bool developer)
    {
        var window=Create(developer);window.Show();Dispatcher.UIThread.RunJobs();
        var dialog=window.ShowEnterpriseSettings(developer);Dispatcher.UIThread.RunJobs();
        try
        {
            var cancel=Find<Button>(dialog,"dialog-cancel");
            Assert.True(cancel.IsFocused);
            foreach(var contrast in new[]{true,false})
            {
                SetPreferences(window,new(2,contrast));Invoke(window,"RefreshOpenDialogs");Dispatcher.UIThread.RunJobs();
                Assert.True(cancel.IsFocused);
                Assert.Equal(3,cancel.BorderThickness.Left);
                var border=Assert.IsAssignableFrom<ISolidColorBrush>(cancel.BorderBrush).Color;
                var background=Assert.IsAssignableFrom<ISolidColorBrush>(cancel.Background).Color;
                Assert.True(LauncherVisualTokens.ContrastRatio(border,background)>=3);
                Assert.Equal(contrast?Colors.Yellow:developer?LauncherVisualTokens.PoscoLightBlue:LauncherVisualTokens.Accent,border);
            }
        }
        finally{dialog.Close();window.Close();}
    }

    public static IEnumerable<object[]> StressStates =>
        from developer in new[]{false,true}
        from size in new[]{new Size(640,360),new Size(854,400)}
        from contrast in new[]{false,true}
        from state in new[]{GeneralLauncherState.Ready,GeneralLauncherState.Working,GeneralLauncherState.RecoverableError,GeneralLauncherState.RuntimeBlocked}
        select new object[]{developer,size.Width,size.Height,contrast,state};

    [AvaloniaTheory][MemberData(nameof(StressStates))]
    public void LargeTextKeepsEveryActionAndWorkspaceUsable(bool developer,double width,double height,bool contrast,GeneralLauncherState state)
    {
        var window=Create(developer,2,contrast,state);window.Show();window.Width=width;window.Height=height;Dispatcher.UIThread.RunJobs();
        try
        {
            var operation=Presentation(window);
            if(state==GeneralLauncherState.RecoverableError)
            {
                operation.ErrorCode="storage-failed";operation.SupportId=new string('a',32);
                operation.Retry=new(LauncherUiOperation.Check,"demo","prod","stable",null);
                operation.Title="선택한 프로젝트의 배포 정보를 확인하지 못했습니다. 네트워크 연결 상태를 확인한 뒤 같은 작업을 다시 시도해 주세요.";
            }
            else if(state==GeneralLauncherState.RuntimeBlocked)
                operation.Title="프로그램 실행 상태를 확인한 다음 다시 시도해 주세요.";
            else if(state==GeneralLauncherState.Working)
            {
                operation.Begin(LauncherUiOperation.Update);operation.Stage("Download");operation.Percent=37;
                operation.Title="다운로드 중 · 파일 1234/5678 · 123.45 MB/s · 전체 37% · 필요한 파일을 준비하고 있습니다.";
            }
            else
                operation.Title="선택한 프로젝트의 설치 상태를 확인했습니다.";
            Invoke(window,"BuildEnterprise");Dispatcher.UIThread.RunJobs();

            foreach(var id in new[]{"settings","primary-action","status-check","update","retry-operation"})
            {
                var button=window.GetVisualDescendants().OfType<Button>().SingleOrDefault(b=>AutomationProperties.GetAutomationId(b)==id);
                if(button is not null)WithinWindow(window,button);
            }
            var workspace=Find<ScrollViewer>(window,"workspace-scroll");
            Assert.True(workspace.Viewport.Height>=48,$"The workspace must retain a readable scroll viewport; actual {workspace.Viewport}.");
            WithinWindow(window,Find<ScrollViewer>(window,"action-details"));
        }
        finally{window.Close();}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void LargeTextKeyboardTraversalReachesActionsAndScrollableDetails(bool developer)
    {
        var window=Create(developer);window.Show();window.Width=854;window.Height=400;Dispatcher.UIThread.RunJobs();
        try
        {
            var expander=Find<Expander>(window,developer?"maintenance":"release-help");
            expander.IsExpanded=true;Dispatcher.UIThread.RunJobs();
            Find<Button>(window,"settings").Focus(NavigationMethod.Tab);
            foreach(var modifiers in new[]{Avalonia.Input.RawInputModifiers.None,Avalonia.Input.RawInputModifiers.Shift})
            {
                var reached=new HashSet<string>();var scrolledDetails=false;
                for(var i=0;i<70;i++)
                {
                    window.KeyPressQwerty(PhysicalKey.Tab,modifiers);Dispatcher.UIThread.RunJobs();
                    if(window.FocusManager!.GetFocusedElement() is not Control focused)continue;
                    var identified=focused.GetSelfAndVisualAncestors().OfType<Control>()
                        .FirstOrDefault(c=>!string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(c)));
                    if(identified is not null)reached.Add(AutomationProperties.GetAutomationId(identified)!);
                    WithinWindow(window,focused);
                    if(AutomationProperties.GetAutomationId(focused)=="action-details" && !scrolledDetails)
                    {
                        var details=(ScrollViewer)focused;
                        Assert.True(details.Extent.Height>details.Viewport.Height);
                        window.KeyPressQwerty(PhysicalKey.PageDown,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
                        Assert.True(details.Offset.Y>0);
                        window.KeyPressQwerty(PhysicalKey.Home,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
                        scrolledDetails=true;
                    }
                }
                foreach(var id in new[]{"primary-action","status-check","action-details","settings",developer?"maintenance":"release-help"})Assert.True(reached.Contains(id),$"{modifiers} did not reach {id}: {string.Join(", ",reached)}");
                if(developer)foreach(var id in new[]{"update","repair","rollback","open-folder","export-logs","details-tabs"})Assert.Contains(id,reached);
                Assert.True(scrolledDetails);
            }
        }
        finally{window.Close();}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void LargeTextSettingsDialogKeepsFooterAndKeyboardCycleUsable(bool developer)
    {
        var window=Create(developer);window.Show();Dispatcher.UIThread.RunJobs();
        var dialog=window.ShowEnterpriseSettings(developer);dialog.Width=640;dialog.Height=360;Dispatcher.UIThread.RunJobs();
        try
        {
            WithinWindow(dialog,Find<Button>(dialog,"dialog-cancel"));WithinWindow(dialog,Find<Button>(dialog,"dialog-confirm"));
            var reached=new HashSet<string>();
            for(var i=0;i<30;i++)
            {
                dialog.KeyPressQwerty(PhysicalKey.Tab,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
                var focused=Assert.IsAssignableFrom<Control>(dialog.FocusManager!.GetFocusedElement());
                WithinWindow(dialog,focused);
                reached.Add(AutomationProperties.GetAutomationId(focused)??"");
            }
            foreach(var id in new[]{"text-scale","high-contrast","settings-export","dialog-cancel","dialog-confirm"})Assert.Contains(id,reached);
            dialog.KeyPressQwerty(PhysicalKey.Escape,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();Assert.False(dialog.IsVisible);
        }
        finally{dialog.Close();window.Close();}
    }

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public void EmptyCatalogAtLargeTextKeepsRefreshKeyboardReachable(bool developer)
    {
        var model=new LauncherDashboardViewModel {Config=new LauncherConfig {ClientProfile=developer?"developer":"general"},GeneralState=GeneralLauncherState.RecoverableError};
        var window=new MainWindow(new("/nonexistent/accessibility-empty.json",LauncherConfigSource.Missing,false),model,new(2,true),false);
        window.Show();window.Width=640;window.Height=360;Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(Find<Button>(window,"primary-action").IsEnabled);
            Find<Button>(window,"settings").Focus(NavigationMethod.Tab);
            var reached=false;
            for(var i=0;i<30;i++)
            {
                window.KeyPressQwerty(PhysicalKey.Tab,Avalonia.Input.RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
                if(window.FocusManager!.GetFocusedElement() is Control focused && AutomationProperties.GetAutomationId(focused)=="empty-refresh")
                {
                    WithinWindow(window,focused);reached=true;break;
                }
            }
            Assert.True(reached);
        }
        finally{window.Close();}
    }
}
