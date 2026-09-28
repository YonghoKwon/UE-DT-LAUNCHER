using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UeDtLauncher.Gui;
public sealed partial class MainWindow
{
    private readonly List<Window> _openDialogs=[];
    private void RefreshOpenDialogs()
    {
        foreach(var dialog in _openDialogs)
        {
            dialog.Background=PageBrush;
            dialog.RequestedThemeVariant=RequestedThemeVariant;
            dialog.Classes.Set("high-contrast",HighContrast);
            foreach(var text in dialog.GetVisualDescendants().OfType<TextBlock>())text.Foreground=Fg();
            foreach(var box in dialog.GetVisualDescendants().OfType<TextBox>()){box.Foreground=Fg();box.Background=SurfaceBrush;}
            foreach(var combo in dialog.GetVisualDescendants().OfType<ComboBox>()){combo.Foreground=Fg();combo.Background=SurfaceBrush;}
            foreach(var check in dialog.GetVisualDescendants().OfType<CheckBox>())check.Foreground=Fg();
            foreach(var button in dialog.GetVisualDescendants().OfType<Button>())
            {
                var primary=button.Classes.Contains("posco-primary");
                button.Foreground=HighContrast||primary?Brushes.White:Fg();
                button.Background=HighContrast?Brushes.Black:primary?LauncherVisualTokens.Brush(LauncherVisualTokens.Accent):SurfaceBrush;
                button.BorderBrush=HighContrast?Brushes.White:LauncherVisualTokens.Border(IsDeveloper);
                foreach(var text in button.GetVisualDescendants().OfType<TextBlock>())text.Foreground=button.Foreground;
            }
        }
    }
    private Window AccessibleDialog(string title,Control body,Button cancel,params Button[] actions)
    {
        var origin=FocusManager?.GetFocusedElement() as Control;
        var originId=origin is null?null:AutomationProperties.GetAutomationId(origin);
        var screen=Screens.ScreenFromWindow(this)??Screens.Primary;
        var maxWidth=(screen?.WorkingArea.Width??1200)/(screen?.Scaling??1)-32;
        var maxHeight=(screen?.WorkingArea.Height??800)/(screen?.Scaling??1)-80;
        var dialog=new Window {Title=title,Width=Math.Min(640,maxWidth),Height=Math.Min(540,maxHeight),MinWidth=Math.Min(360,maxWidth),MinHeight=Math.Min(240,maxHeight),
            WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=PageBrush,RequestedThemeVariant=RequestedThemeVariant};
        if(HighContrast)dialog.Classes.Add("high-contrast");
        _openDialogs.Add(dialog);
        cancel.IsCancel=true; cancel.IsDefault=false; foreach(var button in actions)button.IsDefault=false;
        var root=new Grid {RowDefinitions=new("Auto,*,Auto"),RowSpacing=16,Margin=new Thickness(20)};
        KeyboardNavigation.SetTabNavigation(root,KeyboardNavigationMode.Cycle);
        root.Children.Add(Txt(title,20,true));
        var scroll=Identify(new ScrollViewer {Content=body,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},"dialog-body","대화상자 내용");
        root.Children.Add(AtRow(scroll,1));
        var footer=new WrapPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        foreach(var button in new[]{cancel}.Concat(actions)){button.Margin=new Thickness(8,4,0,0);footer.Children.Add(button);}
        root.Children.Add(AtRow(footer,2));dialog.Content=root;
        dialog.Opened+=(_,_)=>Dispatcher.UIThread.Post(()=>cancel.Focus(NavigationMethod.Tab));
        dialog.Closed+=(_,_)=>{_openDialogs.Remove(dialog);Dispatcher.UIThread.Post(()=>FindFocusTarget(originId)?.Focus(NavigationMethod.Tab));};
        return dialog;
    }
    private async Task<bool> ShowConfirmationAsync(string title,Control body,string actionLabel)
    {
        Window? dialog=null;
        var cancel=Identify(SecondaryButton("취소",(_,_)=>dialog!.Close(false),40),"dialog-cancel","취소");
        var accept=Identify(PrimaryButton(actionLabel,(_,_)=>dialog!.Close(true),40),"dialog-confirm",actionLabel);
        dialog=AccessibleDialog(title,body,cancel,accept);
        return await dialog.ShowDialog<bool>(this);
    }
    internal Window ShowEnterpriseSettings(bool developer)
    {
        var scale=Identify(new ComboBox {ItemsSource=new[]{"100%","125%","150%","200%"},SelectedIndex=Array.IndexOf(LauncherUiPreferences.SupportedScales,_preferences.TextScale),MinHeight=40,FontSize=14*_preferences.TextScale},"text-scale","글자 크기");
        var contrast=Identify(new CheckBox {Content="앱 고대비 사용",IsChecked=_preferences.HighContrast,Foreground=Fg(),FontSize=14*_preferences.TextScale},"high-contrast","앱 고대비 사용");
        var body=new StackPanel {Spacing=16,Children={Txt("화면 접근성",18,true),Muted("글자 크기",14),scale,contrast,Muted("운영체제에서 고대비를 요청하면 앱 설정과 관계없이 고대비로 표시합니다.",13),
            KeyValue("프로젝트",_selectedProject.DisplayName),KeyValue("설치 버전",ReadInstalledVersion()??"미설치")}};
        if(developer){body.Children.Add(DeveloperKeyValue("설정 파일",ConfigPath));body.Children.Add(DeveloperKeyValue("선택",_config.Environment+" / "+_config.Channel));}
        body.Children.Add(EnterpriseButton("지원 로그 ZIP 저장","settings-export",(_,_)=>ExportLogsZip(),tracked:false));
        Window? dialog=null;
        var close=Identify(SecondaryButton("닫기",(_,_)=>dialog!.Close(),40),"dialog-cancel","닫기");
        var apply=Identify(PrimaryButton("적용",async (sender,_)=>
        {
            var index=Math.Clamp(scale.SelectedIndex,0,3);
            var value=new LauncherUiPreferences(LauncherUiPreferences.SupportedScales[index],contrast.IsChecked==true);
            if(sender is Button source)source.IsEnabled=false;
            try {await value.SaveAsync();_preferences=value;dialog!.Close();Build();RefreshOpenDialogs();RecordDisplayDiagnostic();}
            catch(Exception ex){MarkError(ex,"화면 설정 저장 실패");}
            finally {if(sender is Button sourceButton)sourceButton.IsEnabled=true;}
        },40),"dialog-confirm","화면 설정 적용");
        dialog=AccessibleDialog(developer?"개발자 설정":"설정",body,close,apply);dialog.Show(this);return dialog;
    }
    private void ShowEnterpriseError(string title,string message)
    {
        var retry=_presentation.Retry;
        Window? dialog=null;
        var close=Identify(SecondaryButton("닫기",(_,_)=>dialog!.Close(),40),"dialog-cancel","닫기");
        var again=Identify(SecondaryButton("다시 시도",async (_,_)=>{dialog!.Close();if(retry is not null&&ReferenceEquals(retry,_presentation.Retry))await RetryCurrentAsync();else await RefreshInstallStatusAsync();},40),"dialog-retry","같은 작업 다시 시도");
        var copy=Identify(SecondaryButton("지원 ID 복사",async (_,_)=>{if(Clipboard is not null)await Clipboard.SetTextAsync(_presentation.SupportId??"");},40),"copy-support-id","지원 ID 복사");
        dialog=AccessibleDialog(title,new StackPanel {Spacing=14,Children={Txt(message,14,false),copy}},close,again);dialog.Show(this);
    }
    private static int SemanticTabIndex(string id) => id switch
    {
        "project-search"=>10,"project-list" or "compact-project"=>20,"catalog-refresh"=>21,
        "filter-환경"=>30,"filter-채널"=>31,"filter-버전 정책"=>32,"filter-exact-version" or "filter-요청 버전"=>33,
        "primary-action"=>40,"update"=>41,"status-check"=>42,"maintenance"=>50,"repair"=>51,"rollback"=>52,
        "cache-clear"=>53,"backup-cleanup"=>54,"open-folder"=>55,"export-logs"=>56,"details-tabs"=>60,
        "release-help"=>50,"retry-operation"=>43,"action-details"=>44,"operation-log"=>80,"settings"=>90,_=>70
    };
}
