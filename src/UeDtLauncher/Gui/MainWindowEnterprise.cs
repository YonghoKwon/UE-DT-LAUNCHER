using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private readonly LauncherOperationPresentation _presentation = new();
    private LauncherUiPreferences _preferences = new();
    private readonly List<Control> _selectionControls = [];
    private TextBlock? _serviceLabel;
    private TextBlock? _stageLabel;
    private TextBlock? _announcer;
    private ListBox? _enterpriseProjects;
    private ScrollViewer? _enterpriseScroll;
    private Bitmap? _brandLogo;
    private bool _building;
    private string? _configDraft;
    private ScrollViewer? _actionDetails;
    private string? _lastDisplayDiagnostic;
    private bool _maintenanceExpanded;
    private bool _helpExpanded;
    private int _detailsTabIndex;
    private bool HighContrast => _preferences.HighContrast || PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
    private IBrush PageBrush => HighContrast ? Brushes.Black : LauncherVisualTokens.Background(IsDeveloper);
    private IBrush SurfaceBrush => HighContrast ? Brushes.Black : LauncherVisualTokens.Surface(IsDeveloper);
    private bool HasProject => _viewModel.ProjectsForProfile().Any(p => p.ProjectId == _selectedProject.ProjectId);

    private T Identify<T>(T control, string id, string name) where T : Control
    {
        AutomationProperties.SetAutomationId(control,id); AutomationProperties.SetName(control,name); control.TabIndex=SemanticTabIndex(id); return control;
    }
    private Button EnterpriseButton(string text,string id,EventHandler<Avalonia.Interactivity.RoutedEventArgs> action,bool primary=false,bool tracked=true)
    {
        var button=primary?PrimaryButton(text,action,48):SecondaryButton(text,action,40);
        Identify(button,id,text); if(tracked) Track(button); return button;
    }
    private Control EnterpriseCard(Control content,double padding=16) => new Border
    {
        Child=content, Padding=new Thickness(padding), Background=SurfaceBrush,
        BorderBrush=HighContrast?Brushes.White:LauncherVisualTokens.Border(IsDeveloper), BorderThickness=new Thickness(1),
        CornerRadius=new CornerRadius(LauncherVisualTokens.RadiusCard)
    };
    private void BuildEnterprise()
    {
        if(_building) return;
        _building=true;
        try
        {
            var focused=FocusManager?.GetFocusedElement() as Control;
            var focusId=focused is null?null:AutomationProperties.GetAutomationId(focused);
            if(focused is ListBoxItem && focusId?.StartsWith("project-",StringComparison.Ordinal)==true)
                focusId="project-"+_selectedProject.ProjectId;
            var caret=focused is TextBox tb?tb.CaretIndex:0;
            var selectionStart=focused is TextBox start?start.SelectionStart:0;
            var selectionEnd=focused is TextBox end?end.SelectionEnd:0;
            var offset=_enterpriseScroll?.Offset ?? default;
            _configDraft=_configPathBox?.Text ?? _configDraft ?? _startupOptions.ConfigPath;
            _configPathBox=null;
            _actionButtons.Clear(); _selectionControls.Clear(); _nextTabIndex=0;
            Title=IsDeveloper?"POSCO DX · DT Launcher — 개발자":"POSCO DX · DT Launcher";
            RequestedThemeVariant=IsDeveloper||HighContrast?Avalonia.Styling.ThemeVariant.Dark:Avalonia.Styling.ThemeVariant.Light;
            var screen=Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var availableWidth=screen?.WorkingArea.Width / (screen?.Scaling ?? 1) ?? 1280;
            var availableHeight=screen?.WorkingArea.Height / (screen?.Scaling ?? 1) ?? 760;
            MinWidth=Math.Min(640,availableWidth-32); MinHeight=Math.Min(360,availableHeight-64);
            if(!_windowMetricsInitialized)
            {
                Width=Math.Min(IsDeveloper?1280:1120,availableWidth-32);
                Height=Math.Min(740,availableHeight-64); _windowMetricsInitialized=true;
            }
            var width=CurrentLayoutWidth(); _layoutBucket=GeneralLayoutBucket(width);
            var wide=width>=1100; var projects=_viewModel.ProjectsForProfile().ToList();
            var sidebar=wide && (IsDeveloper || projects.Count>1);
            Background=PageBrush;
            Classes.Set("high-contrast",HighContrast);
            if(!IsDeveloper)_logBox=null;
            var root=new Grid { RowDefinitions=new("Auto,*,Auto"), RowSpacing=12, Background=PageBrush, Margin=new Thickness(width<800?12:20) };
            root.Children.Add(EnterpriseHeader());
            var work=new Grid { ColumnDefinitions=new(sidebar?"248,*":"*"),ColumnSpacing=20 };
            if(sidebar)work.Children.Add(EnterpriseProjectList());
            var content=new StackPanel { Spacing=16 };
            if(!sidebar && projects.Count>1) content.Children.Add(CompactProjectSelector());
            if(IsDeveloper) content.Children.Add(EnterpriseFilters());
            if(!HasProject)
            {
                content.Children.Add(EnterpriseCard(new StackPanel {Spacing=12,Children={Txt("사용 가능한 프로젝트가 없습니다",24,true),Muted("이 PC에 허용된 배포를 확인하거나 관리자에게 문의해 주세요.",14),EnterpriseButton("배포 다시 확인","empty-refresh",async (_,_)=>await RefreshCatalog(true))}}));
            }
            else
            {
                content.Children.Add(EnterpriseOverview());
                if(IsDeveloper)
                {
                    var more=new Expander { Header="유지보수 및 진단",Content=EnterpriseMaintenance(),IsExpanded=_maintenanceExpanded };
                    more.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty)_maintenanceExpanded=more.IsExpanded;};
                    Identify(more,"maintenance","유지보수 및 진단"); content.Children.Add(more);
                    var details=ReleaseInfo(); var logs=EnterpriseLogs();
                    if(wide)
                    {
                        var split=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=16}; split.Children.Add(details);split.Children.Add(At(logs,1));content.Children.Add(split);
                    }
                    else
                    {
                        var tabs=Identify(new TabControl {ItemsSource=new[]{new TabItem {Header="배포 정보",Content=details},new TabItem {Header="작업 기록",Content=logs}},SelectedIndex=_detailsTabIndex},"details-tabs","배포 정보와 작업 기록");
                        tabs.SelectionChanged+=(_,_)=>{if(!_building)_detailsTabIndex=Math.Max(0,tabs.SelectedIndex);};
                        content.Children.Add(tabs);
                    }
                }
                else
                {
                    var help=new Expander {Header="릴리스 설명 및 도움말",IsExpanded=_helpExpanded,Content=new StackPanel {Spacing=12,Children={Txt(_releaseNotes,14,false),Muted("프로그램이 실행 중이면 정상 종료 후 다시 확인해 주세요. 창을 닫아도 실행 중인 프로그램은 종료되지 않습니다.",14)}}};
                    Identify(help,"release-help","릴리스 설명 및 도움말");
                    help.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty)_helpExpanded=help.IsExpanded;};
                    content.Children.Add(EnterpriseCard(help));
                }
            }
            _enterpriseScroll=Identify(new ScrollViewer {Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},"workspace-scroll","프로젝트 작업 공간");
            work.Children.Add(At(_enterpriseScroll,sidebar?1:0)); root.Children.Add(AtRow(work,1));
            root.Children.Add(AtRow(EnterpriseActionBar(),2));
            Content=root;
            UpdateActionDetailsLimit(); SetBusy(_running); RefreshPresentation();
            Dispatcher.UIThread.Post(()=>
            {
                if(_enterpriseScroll is not null)_enterpriseScroll.Offset=offset;
                if(focusId is null)return;
                var control=FindFocusTarget(focusId);
                if(control is TextBox box) {box.CaretIndex=Math.Min(caret,box.Text?.Length??0);box.SelectionStart=Math.Min(selectionStart,box.Text?.Length??0);box.SelectionEnd=Math.Min(selectionEnd,box.Text?.Length??0);}
                control?.Focus(NavigationMethod.Tab);
            });
        }
        finally {_building=false;}
    }
    private Control EnterpriseHeader()
    {
        _brandLogo ??= new Bitmap(AssetLoader.Open(new Uri("avares://UeDtLauncher/Assets/Branding/posco-dx-korean.png")));
        var grid=new Grid {ColumnDefinitions=new("Auto,*,Auto"),ColumnSpacing=14};
        var logo=Identify(new Image {Source=_brandLogo,Width=128,Height=36,Stretch=Stretch.Uniform},"brand-logo","포스코DX");
        grid.Children.Add(new Border {Background=Brushes.White,Padding=new Thickness(12,8),CornerRadius=new CornerRadius(6),Child=logo});
        grid.Children.Add(At(new StackPanel {Spacing=2,VerticalAlignment=VerticalAlignment.Center,Children={Txt("DT Launcher",20,true),Muted(IsDeveloper?"개발자 배포 작업 공간":"디지털 트윈 프로그램",12)}},1));
        _serviceLabel=Identify(Muted(_agentState,12),"service-status","업데이트 서비스 상태");
        var right=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12,VerticalAlignment=VerticalAlignment.Center};
        if(CurrentLayoutWidth()>=800)right.Children.Add(_serviceLabel);
        var settings=EnterpriseButton("설정","settings",(_,_)=>SettingsDialog(IsDeveloper),tracked:false);right.Children.Add(settings);grid.Children.Add(At(right,2));
        return grid;
    }
    private Control EnterpriseProjectList()
    {
        var grid=new Grid {RowDefinitions=new("Auto,Auto,*"),RowSpacing=12};
        var heading=new Grid {ColumnDefinitions=new("*,Auto")};heading.Children.Add(Txt("프로젝트",18,true));
        heading.Children.Add(At(EnterpriseButton("새로고침","catalog-refresh",async (_,_)=>await RefreshCatalog(true)),1));grid.Children.Add(heading);
        var search=Identify(new TextBox {Text=_search,Watermark="프로젝트 검색",MinHeight=40,FontSize=14*_preferences.TextScale},"project-search","프로젝트 검색");
        search.TextChanged+=(_,_)=>{_search=search.Text??"";RenderEnterpriseProjects();};grid.Children.Add(AtRow(search,1));
        _enterpriseProjects=Identify(new ListBox {Background=Brushes.Transparent,BorderThickness=new Thickness(0),SelectionMode=SelectionMode.Single},"project-list","프로젝트 선택");
        RenderEnterpriseProjects();
        _enterpriseProjects.SelectionChanged+=(_,_)=>
        {
            if(_building||_running||_enterpriseProjects.SelectedItem is not ListBoxItem {Tag:ProjectUiConfig project})return;
            if(_selectedProject.ProjectId==project.ProjectId)return;
            _selectedProject=project;_config.ProjectId=project.ProjectId;SelectionChanged();Build();
        };
        _selectionControls.Add(_enterpriseProjects);grid.Children.Add(AtRow(_enterpriseProjects,2));return grid;
    }
    private void RenderEnterpriseProjects()
    {
        if(_enterpriseProjects is null)return;
        var previous=_building; _building=true;
        try
        {
            var items=VisibleProjects().Select(p=>Identify(new ListBoxItem {Tag=p,Padding=new Thickness(12),Margin=new Thickness(0,0,0,8),Content=new StackPanel {Spacing=6,Children={Label(p.DisplayName,15,p.ProjectId==_selectedProject.ProjectId?Brushes.White:Fg(),true),Label(p.ProjectId,12,p.ProjectId==_selectedProject.ProjectId?Brushes.White:MutedBrush())}}},"project-"+p.ProjectId,p.DisplayName)).ToList();
            _enterpriseProjects.ItemsSource=items;
            _enterpriseProjects.SelectedItem=items.FirstOrDefault(i=>((ProjectUiConfig)i.Tag!).ProjectId==_selectedProject.ProjectId);
        }
        finally {_building=previous;}
    }
    private Control EnterpriseFilters()
    {
        var wrap=new WrapPanel {Orientation=Orientation.Horizontal};
        foreach(var control in new[]{ComboLine("환경",_config.Environment,["prod","dev"],v=>{_config.Environment=v;SelectionChanged();}),ComboLine("채널",_config.Channel,["stable","beta","dev"],v=>{_config.Channel=v;SelectionChanged();}),ComboLine("버전 정책",_config.VersionPolicy,["latest","exact"],v=>{_config.VersionPolicy=v;SelectionChanged();})})
        {control.Width=220;control.Margin=new Thickness(0,0,12,8);wrap.Children.Add(control);}
        if(_config.VersionPolicy=="exact"){var exact=ExactVersionLine();exact.Width=240;exact.Margin=new Thickness(0,0,0,8);wrap.Children.Add(exact);}
        return EnterpriseCard(wrap,12);
    }
    private Control EnterpriseOverview()
    {
        var narrow=CurrentLayoutWidth()<800||_preferences.TextScale>1.5;
        var grid=new Grid {ColumnDefinitions=new(narrow?"*":"*,164"),ColumnSpacing=20};
        var stack=new StackPanel {Spacing=10};stack.Children.Add(Txt(_selectedProject.DisplayName,24,true));
        var status=new Grid {ColumnDefinitions=new("Auto,*"),ColumnSpacing=8};
        var icon=LauncherIconFactory.Create(_viewModel.GeneralState==GeneralLauncherState.Ready?LauncherIconKind.Check:_viewModel.GeneralState==GeneralLauncherState.RecoverableError?LauncherIconKind.Warning:LauncherIconKind.Shield,20,Fg());
        AutomationProperties.SetAccessibilityView(icon,AccessibilityView.Raw);status.Children.Add(icon);status.Children.Add(At(Txt(_installState,16,true),1));
        stack.Children.Add(status);stack.Children.Add(Muted(_installDetail,14));
        var versions=new WrapPanel {Orientation=Orientation.Horizontal};
        versions.Children.Add(new StackPanel {Margin=new Thickness(0,0,32,0),Children={Muted(!IsDeveloper && _viewModel.ProjectStatus is {IsInstalled:false,PreviousInstallation:not null}?"기존 설치 버전":"설치 버전",12),Txt(ReadInstalledVersion()??"미설치",16,true)}});
        versions.Children.Add(new StackPanel {Children={Muted(IsDeveloper?"선택 배포 버전":"최신 배포 버전",12),Txt(LatestCatalogVersion()??"확인 필요",16,true)}});
        stack.Children.Add(versions);grid.Children.Add(stack);
        if(!narrow&&!HighContrast)
        {
            var visual=ProjectHeroVisual(ProjectVisualResolver.Resolve(_selectedProject,ConfigPath,ProjectVisualKind.Hero)); visual.Height=108;
            AutomationProperties.SetAccessibilityView(visual,AccessibilityView.Raw);grid.Children.Add(At(visual,1));
        }
        return EnterpriseCard(grid,20);
    }
    private Control EnterpriseMaintenance()
    {
        var wrap=new WrapPanel {Orientation=Orientation.Horizontal};
        var buttons=new[]{EnterpriseButton("검증/복구","repair",async (_,_)=>await RunAsync(true,false)),EnterpriseButton("백업 복원","rollback",async (_,_)=>await RollbackLatestAsync()),
            EnterpriseButton("캐시 정리","cache-clear",(_,_)=>ClearCache()),EnterpriseButton("백업 정리","backup-cleanup",(_,_)=>CleanupBackups()),
            EnterpriseButton("설치 폴더","open-folder",(_,_)=>OpenInstallFolder(),tracked:false),EnterpriseButton("로그 ZIP","export-logs",(_,_)=>ExportLogsZip(),tracked:false)};
        foreach(var b in buttons){b.Margin=new Thickness(0,8,8,0);wrap.Children.Add(b);}return wrap;
    }
    private Control EnterpriseLogs()
    {
        _logBox=Identify(new TextBox {Text=_presentation.LogText,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=180,MaxHeight=360,FontSize=12*_preferences.TextScale,FontFamily=new FontFamily("Cascadia Mono,Consolas"),Foreground=Fg(),Background=SurfaceBrush},"operation-log","작업 기록");
        return EnterpriseCard(new StackPanel {Spacing=12,Children={Txt("작업 기록",18,true),_logBox}});
    }
    private Control EnterpriseActionBar()
    {
        var panel=new StackPanel {Spacing=12};var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        var primary=EnterpriseButton(IsDeveloper?"실행":_viewModel.PrimaryActionText,"primary-action",async (_,_)=>{if(IsDeveloper)await RunAsync(false,true);else await ExecutePrimaryActionAsync();},true);
        primary.Tag="general-primary-action"; primary.IsEnabled=HasProject&&_viewModel.PrimaryAction!=PrimaryActionKind.Disabled&&!_running;
        if(IsDeveloper)primary.IsEnabled=HasProject&&!_running;
        primary.HotKey=new KeyGesture(Key.F5);actions.Children.Add(primary);
        if(IsDeveloper)actions.Children.Add(EnterpriseButton("업데이트","update",async (_,_)=>await RunAsync(false,false)));
        var check=EnterpriseButton(_viewModel.GeneralState==GeneralLauncherState.RecoverableError?"문제 해결":"상태 확인","status-check",async (_,_)=>{if(!IsDeveloper&&_viewModel.GeneralState==GeneralLauncherState.RecoverableError)await TroubleshootAsync();else await RefreshSelectionStatusAsync();});
        check.HotKey=new KeyGesture(Key.F6);actions.Children.Add(check);
        if(_viewModel.GeneralState!=GeneralLauncherState.RuntimeBlocked && _presentation.ErrorCode is not null && _presentation.Retry is not null)actions.Children.Add(EnterpriseButton("다시 시도","retry-operation",async (_,_)=>await RetryCurrentAsync()));
        foreach(var item in actions.Children)item.Margin=new Thickness(0,0,8,0);
        panel.Children.Add(actions);
        _statusText=Identify(Txt(_presentation.Title,14,true),"operation-status","작업 상태");
        _percentText=Muted("",12);_stageLabel=Muted("",12);
        _progress=Identify(new ProgressBar {Minimum=0,Maximum=100,Height=6},"operation-progress","작업 진행률");
        if(_announcer?.Parent is Panel oldParent)oldParent.Children.Remove(_announcer);
        _announcer ??= Identify(new TextBlock {Height=1,Opacity=0,IsHitTestVisible=false},"operation-announcement","작업 상태 알림");
        AutomationProperties.SetLiveSetting(_announcer,AutomationLiveSetting.Polite);
        _statusText.MaxLines=2;
        panel.Children.Add(_statusText);var detailsPanel=new StackPanel {Spacing=8};var detail=new Grid {ColumnDefinitions=new("*,Auto")};detail.Children.Add(_stageLabel);detail.Children.Add(At(_percentText,1));detailsPanel.Children.Add(detail);detailsPanel.Children.Add(_progress);panel.Children.Add(_announcer);
        if(CurrentLayoutWidth()<800 && _serviceLabel is not null)detailsPanel.Children.Add(_serviceLabel);
        if(_presentation.SupportId is not null)detailsPanel.Children.Add(Muted("지원 ID: "+_presentation.SupportId,12));
        _actionDetails=Identify(new ScrollViewer {Content=detailsPanel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},"action-details","작업 상세 안내");
        panel.Children.Add(_actionDetails);
        return EnterpriseCard(panel,12);
    }
    private void UpdateActionDetailsLimit()
    {
        if(_actionDetails is not null)_actionDetails.MaxHeight=Math.Max(48,Math.Min(140,ClientSize.Height*0.18));
    }
    private Control? FindFocusTarget(string? id)
    {
        var controls=this.GetVisualDescendants().OfType<Control>().Where(c=>c.IsEffectivelyVisible&&c.IsEnabled&&c.Focusable).ToList();
        foreach(var candidate in new[]{id,"project-list","compact-project","status-check","settings"})
        {
            if(candidate is null)continue;
            var found=controls.FirstOrDefault(c=>AutomationProperties.GetAutomationId(c)==candidate);
            if(found is not null)return found;
        }
        return null;
    }
    private void RecordDisplayDiagnostic()
    {
        var screen=Screens.ScreenFromWindow(this)??Screens.Primary;
        var value=System.Text.Json.JsonSerializer.Serialize(new {profile=_config.ClientProfile,screenPixels=screen?.Bounds.ToString(),
            workingPixels=screen?.WorkingArea.ToString(),renderScaling=RenderScaling,clientDip=ClientSize.ToString(),
            textScale=_preferences.TextScale,highContrast=HighContrast});
        if(value==_lastDisplayDiagnostic)return;
        _lastDisplayDiagnostic=value;_fileLogger?.Log("UiDisplay",value);
    }
    private void SetStatus(string message)
    {
        _presentation.Title=message;RefreshPresentation();
    }
    private void RefreshPresentation()
    {
        if(_statusText is not null)_statusText.Text=_presentation.Title;
        if(_percentText is not null)_percentText.Text=_presentation.Percent is { } p?$"{p:0}%":_running?"진행 중":"대기";
        if(_progress is not null){_progress.IsIndeterminate=_presentation.Percent is null && _running;_progress.Value=_presentation.Percent??0;}
        if(_stageLabel is not null)_stageLabel.Text="현재 단계 · "+_presentation.CurrentStage;
        if(_announcer is not null && _announcer.Text!=_presentation.Announcement)_announcer.Text=_presentation.Announcement;
    }
}
