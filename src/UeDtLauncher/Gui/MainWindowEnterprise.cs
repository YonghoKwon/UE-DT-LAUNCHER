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
    private Control? _projectSearchEmpty;
    private ScrollViewer? _enterpriseScroll;
    private Bitmap? _brandLogo;
    private bool _building;
    private string? _configDraft;
    private ScrollViewer? _actionDetails;
    private string? _lastDisplayDiagnostic;
    private readonly string _displaySessionId=Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _displaySessionStarted=DateTimeOffset.UtcNow;
    private bool _maintenanceExpanded;
    private bool _helpExpanded;
    private TextBlock? _downloadCancellationGuidance;
    private int _detailsTabIndex;
    private bool HighContrast => _preferences.HighContrast || PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
    private IBrush PageBrush => HighContrast ? Brushes.Black : LauncherVisualTokens.Brush(LauncherVisualTokens.PageColor(IsDeveloper, false));
    private IBrush SurfaceBrush => HighContrast ? Brushes.Black : LauncherVisualTokens.Brush(LauncherVisualTokens.SurfaceColor(IsDeveloper, false));
    private bool HasProject => _viewModel.ProjectsForProfile().Any(p => p.ProjectId == _selectedProject.ProjectId);

    private T Identify<T>(T control, string id, string name) where T : Control
    {
        AutomationProperties.SetAutomationId(control,id); AutomationProperties.SetName(control,name); control.TabIndex=SemanticTabIndex(id); return control;
    }
    private Button EnterpriseButton(string text,string id,EventHandler<Avalonia.Interactivity.RoutedEventArgs> action,bool primary=false,bool tracked=true,LauncherIconKind? icon=null)
    {
        var button=primary?PrimaryButton(text,action,48):SecondaryButton(text,action,40);
        if (icon is { } kind && button.Content is TextBlock label &&
            (CurrentLayoutWidth() >= 800 || _preferences.TextScale <= 1.5))
        {
            button.Content = null;
            button.Content = IconLabel(kind, label, LauncherVisualTokens.SpaceLg);
        }
        Identify(button,id,text); if(tracked) Track(button); return button;
    }

    private static Control IconLabel(LauncherIconKind kind, TextBlock label, double size)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = LauncherVisualTokens.SpaceSm };
        row.Children.Add(LauncherIconFactory.Create(kind, size, label.Foreground!));
        row.Children.Add(At(label, 1));
        return row;
    }
    private T ScaleHeader<T>(T control) where T : TemplatedControl
    {
        // String headers use the template's inherited font rather than Label().
        control.FontSize=LauncherVisualTokens.FontBody*_preferences.TextScale;
        return control;
    }
    private void BuildEnterprise()
    {
        if(_building) return;
        _building=true;
        try
        {
            var focused=FocusManager?.GetFocusedElement() as Control;
            var focusId=_workspaceFocus ?? (focused is null?null:AutomationProperties.GetAutomationId(focused));
            _workspaceFocus = null;
            if(focused is ListBoxItem && focusId?.StartsWith("project-",StringComparison.Ordinal)==true)
                focusId="project-"+_selectedProject.ProjectId;
            var caret=focused is TextBox tb?tb.CaretIndex:0;
            var selectionStart=focused is TextBox start?start.SelectionStart:0;
            var selectionEnd=focused is TextBox end?end.SelectionEnd:0;
            var offset=_enterpriseScroll?.Offset ?? default;
            _configDraft=_configPathBox?.Text ?? _configDraft ?? _startupOptions.ConfigPath;
            _configPathBox=null;
            _enterpriseProjects=null; _projectSearchEmpty=null;
            _projectCardsView = null;
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
            var sidebar=wide && (IsDeveloper || (!_hasAuthorizedCatalog && projects.Count>1));
            Background=PageBrush;
            Classes.Set("high-contrast",HighContrast);
            Resources["LauncherProjectSelectionBrush"] = LauncherVisualTokens.Brush(
                LauncherVisualTokens.ProjectSelectionColor(IsDeveloper, HighContrast));
            if(!IsDeveloper)_logBox=null;
            var root=new Grid { RowDefinitions=new("Auto,*,Auto"), RowSpacing=LauncherVisualTokens.SpaceMd, Background=PageBrush, Margin=new Thickness(width<800?LauncherVisualTokens.SpaceMd:LauncherVisualTokens.SpaceXl) };
            root.Children.Add(EnterpriseHeader());
            var work=new Grid { ColumnDefinitions=new(sidebar?"248,*":"*"),ColumnSpacing=20 };
            if(sidebar)work.Children.Add(EnterpriseProjectList());
            var content=new StackPanel { Spacing=LauncherVisualTokens.SpaceLg };
            if(_viewModel.GeneralState==GeneralLauncherState.ConfigurationRequired && (IsDeveloper || _configurationError is null))
            {
                content.Children.Add(Identify(Card(new StackPanel{Spacing=12,Children={Txt("화면 설정 준비",18,true),Muted("관리자에게 프로젝트 ID와 업데이트 서비스 준비 상태를 확인해 주세요. 실행 파일 옆에 화면 설정을 생성한 뒤 다시 확인하세요. 아래 명령의 프로젝트ID를 실제 등록값으로 바꿉니다.",14),ConfigurationExample()}}),"configuration-help","화면 설정 생성 안내"));
            }
            if(!sidebar && projects.Count>1 && !_hasAuthorizedCatalog) content.Children.Add(CompactProjectSelector());
            if(IsDeveloper) content.Children.Add(EnterpriseFilters());
            if (!ShowsProjectCards && _projectListNotice is { } notice && _hasAuthorizedCatalog)
                content.Children.Add(ProjectCardNotice(notice));
            if (HasProjectCards && _showProjectDetails)
                content.Children.Add(ProjectCardsBack());
            if (ShowsProjectCards)
            {
                content.Children.Add(ProjectCards());
            }
            else if (_showStartupSkeleton || (!IsDeveloper && _running && _viewModel.GeneralState == GeneralLauncherState.Checking))
            {
                content.Children.Add(StartupSkeleton());
            }
            else if(!IsDeveloper && _configurationError is not null)
            {
                content.Children.Add(Card(Txt(_installDetail,LauncherVisualTokens.FontBody,false)));
            }
            else if(!HasProject)
            {
                var unavailable = projects.Count != 0 || _configurationError is not null || _presentation.ErrorCode is not null;
                if (!IsDeveloper && _hasAuthorizedCatalog && projects.Count == 0)
                    content.Children.Add(Txt("받을 수 있는 프로그램이 없습니다.", LauncherVisualTokens.FontProject, true));
                content.Children.Add(EmptyState(
                    "deployment-empty", LauncherIconKind.Package,
                    "사용 가능한 프로젝트가 없습니다",
                    !IsDeveloper && _presentation.ErrorCode is not null ? _installDetail : unavailable
                        ? "이 PC에 허용된 배포를 확인하거나 관리자에게 문의해 주세요."
                        : "아직 공개된 버전이 없습니다. 담당자가 공개하면 표시됩니다.",
                    EnterpriseButton("배포 다시 확인","empty-refresh",async (_,_)=>await RefreshSelectionStatusAsync(),primary:!IsDeveloper,icon:LauncherIconKind.Refresh)));
            }
            else
            {
                content.Children.Add(EnterpriseOverview());
                if(IsDeveloper)
                {
                    var more=ScaleHeader(new Expander { Header="유지보수 및 진단",Content=EnterpriseMaintenance(),IsExpanded=_maintenanceExpanded });
                    // Keep template headers in both Tab directions before/after their own content.
                    KeyboardNavigation.SetTabNavigation(more,KeyboardNavigationMode.Local);
                    more.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty)_maintenanceExpanded=more.IsExpanded;};
                    Identify(more,"maintenance","유지보수 및 진단"); content.Children.Add(more);
                    var details=ReleaseInfo(); var logs=EnterpriseLogs();
                    if(wide)
                    {
                        var split=new Grid {ColumnDefinitions=new("*,*"),ColumnSpacing=16}; split.Children.Add(details);split.Children.Add(At(logs,1));content.Children.Add(split);
                    }
                    else
                    {
                        var tabs=Identify(new TabControl {ItemsSource=new[]{ScaleHeader(new TabItem {Header="배포 정보",Content=details}),ScaleHeader(new TabItem {Header="작업 기록",Content=logs})},SelectedIndex=_detailsTabIndex},"details-tabs","배포 정보와 작업 기록");
                        KeyboardNavigation.SetTabNavigation(tabs,KeyboardNavigationMode.Local);
                        tabs.SelectionChanged+=(_,_)=>{if(!_building)_detailsTabIndex=Math.Max(0,tabs.SelectedIndex);};
                        content.Children.Add(tabs);
                    }
                }
                else
                {
                    var help=ScaleHeader(new Expander {Header="릴리스 설명 및 도움말",IsExpanded=_helpExpanded});
                    if (_helpExpanded) help.Content = GeneralReleaseHelp();
                    KeyboardNavigation.SetTabNavigation(help,KeyboardNavigationMode.Local);
                    Identify(help,"release-help","릴리스 설명 및 도움말");
                    help.PropertyChanged+=(_,e)=>
                    {
                        if (e.Property != Expander.IsExpandedProperty) return;
                        _helpExpanded=help.IsExpanded;
                        if (help.IsExpanded) help.Content ??= GeneralReleaseHelp();
                    };
                    content.Children.Add(Card(help));
                }
            }
            if (!IsDeveloper && !ShowsProjectCards && !_showStartupSkeleton && (_presentation.ErrorCode is not null ||
                _viewModel.GeneralState == GeneralLauncherState.RuntimeBlocked ||
                (_viewModel.GeneralState == GeneralLauncherState.UpdateAvailable && SelectedInstallationDamaged)))
                content.Children.Add(GeneralErrorDetails());
            _enterpriseScroll=Identify(new ScrollViewer {Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},"workspace-scroll","프로젝트 작업 공간");
            work.Children.Add(At(_enterpriseScroll,sidebar?1:0)); root.Children.Add(AtRow(work,1));
            if (!ShowsProjectCards || _cardOperation)
                root.Children.Add(AtRow(EnterpriseActionBar(projectCards: ShowsProjectCards),2));
            else
            {
                _statusText = _percentText = _stageLabel = _downloadCancellationGuidance = null;
                _progress = null;
                _actionDetails = null;
            }
            Content=root;
            UpdateActionDetailsLimit(); SetBusy(_running); RefreshPresentation();
            Dispatcher.UIThread.Post(()=>
            {
                if(_enterpriseScroll is not null)_enterpriseScroll.Offset=offset;
                if(focusId is null || !ReferenceEquals(Content, root))return;
                var control=FindFocusTarget(focusId);
                if(control is TextBox box) {box.CaretIndex=Math.Min(caret,box.Text?.Length??0);box.SelectionStart=Math.Min(selectionStart,box.Text?.Length??0);box.SelectionEnd=Math.Min(selectionEnd,box.Text?.Length??0);}
                control?.Focus(NavigationMethod.Tab);
            });
        }
        finally {_building=false;}
    }
    private Control EnterpriseHeader()
    {
        _brandLogo ??= new Bitmap(AssetLoader.Open(new Uri($"avares://{typeof(App).Assembly.GetName().Name}/Assets/Branding/posco-dx-korean.png")));
        var grid=new Grid {ColumnDefinitions=new("Auto,*,Auto"),ColumnSpacing=14};
        var logo=Identify(new Image {Source=_brandLogo,Width=128,Height=36,Stretch=Stretch.Uniform},"brand-logo","포스코DX");
        grid.Children.Add(new Border {Background=Brushes.White,Padding=new Thickness(12,8),CornerRadius=new CornerRadius(6),Child=logo});
        grid.Children.Add(At(new StackPanel {Spacing=LauncherVisualTokens.SpaceXs,VerticalAlignment=VerticalAlignment.Center,Children={Txt("DT Launcher",LauncherVisualTokens.FontSection,true),Muted(IsDeveloper?"개발자 배포 작업 공간":"디지털 트윈 프로그램",LauncherVisualTokens.FontCaption)}},1));
        _serviceLabel=Identify(Muted(_agentState,LauncherVisualTokens.FontCaption),"service-status","업데이트 서비스 상태");
        var right=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12,VerticalAlignment=VerticalAlignment.Center};
        if (CurrentLayoutWidth() >= 800) right.Children.Add(_serviceLabel);
        else if (ShowsProjectCards)
        {
            grid.RowDefinitions = new("Auto,Auto");
            grid.RowSpacing = LauncherVisualTokens.SpaceXs;
            Grid.SetColumnSpan(_serviceLabel, 3);
            grid.Children.Add(AtRow(_serviceLabel, 1));
        }
        var settings=EnterpriseButton("설정","settings",(_,_)=>SettingsDialog(IsDeveloper),tracked:false,icon:LauncherIconKind.Settings);right.Children.Add(settings);grid.Children.Add(At(right,2));
        return grid;
    }
    private Control EnterpriseProjectList()
    {
        var grid=new Grid {RowDefinitions=new("Auto,Auto,*"),RowSpacing=LauncherVisualTokens.SpaceMd};
        var heading=new Grid {ColumnDefinitions=new("*,Auto")};heading.Children.Add(Txt("프로젝트",LauncherVisualTokens.FontSection,true));
        heading.Children.Add(At(EnterpriseButton("새로고침","catalog-refresh",async (_,_)=>await RefreshSelectionStatusAsync(true),icon:LauncherIconKind.Refresh),1));grid.Children.Add(heading);
        var search=Identify(new TextBox {Text=_search,Watermark="프로젝트 검색",MinHeight=40,FontSize=LauncherVisualTokens.FontBody*_preferences.TextScale},"project-search","프로젝트 검색");
        var clearSearch = EnterpriseButton("검색어 지우기", "clear-project-search", (_, _) =>
        {
            search.Text = string.Empty;
            search.Focus();
        }, icon: LauncherIconKind.Clear);
        var empty = EmptyState("project-search-empty", LauncherIconKind.Search, "검색 결과가 없습니다",
            "프로젝트 이름을 다시 입력하거나 검색어를 지워 전체 목록을 확인하세요.", clearSearch);
        empty.VerticalAlignment = VerticalAlignment.Top;
        _projectSearchEmpty = empty;
        search.TextChanged+=(_,_)=>{_search=search.Text??"";RenderEnterpriseProjects();};grid.Children.Add(AtRow(search,1));
        _enterpriseProjects=Identify(new ListBox {Background=Brushes.Transparent,BorderThickness=new Thickness(0),SelectionMode=SelectionMode.Single},"project-list","프로젝트 선택");
        RenderEnterpriseProjects();
        _enterpriseProjects.SelectionChanged+=(_,_)=>
        {
            if(_building||_running||_enterpriseProjects.SelectedItem is not ListBoxItem {Tag:ProjectUiConfig project})return;
            if(_selectedProject.ProjectId==project.ProjectId)return;
            _selectedProject=project;_config.ProjectId=project.ProjectId;SelectionChanged();Build();
        };
        _selectionControls.Add(_enterpriseProjects);
        grid.Children.Add(AtRow(new Grid { Children = { _enterpriseProjects, empty } }, 2));
        return grid;
    }
    private void RenderEnterpriseProjects()
    {
        if(_enterpriseProjects is null || _projectSearchEmpty is not { } empty)return;
        var previous=_building; _building=true;
        try
        {
            var items = VisibleProjects().Select(project =>
            {
                var selected = project.ProjectId == _selectedProject.ProjectId;
                return Identify(new ListBoxItem
                {
                    Tag = project,
                    Classes = { "launcher-project" },
                    Padding = new Thickness(LauncherVisualTokens.SpaceMd),
                    Margin = new Thickness(0, 0, 0, LauncherVisualTokens.SpaceSm),
                    Content = new StackPanel
                    {
                        Spacing = LauncherVisualTokens.SpaceXs,
                        Children =
                        {
                            Label(project.DisplayName, LauncherVisualTokens.FontBody, selected ? Brushes.White : Fg(), true),
                            Label(project.ProjectId, LauncherVisualTokens.FontCaption, selected ? Brushes.White : MutedBrush())
                        }
                    }
                }, "project-" + project.ProjectId, project.DisplayName);
            }).ToList();
            _enterpriseProjects.ItemsSource=items;
            _enterpriseProjects.SelectedItem=items.FirstOrDefault(i=>((ProjectUiConfig)i.Tag!).ProjectId==_selectedProject.ProjectId);
            empty.IsVisible = items.Count == 0 && !string.IsNullOrWhiteSpace(_search) && _viewModel.ProjectsForProfile().Any();
            _enterpriseProjects.IsVisible = !empty.IsVisible;
        }
        finally {_building=previous;}
    }

    private Control EmptyState(string id, LauncherIconKind icon, string title, string detail, Button action)
    {
        var actions = Identify(new WrapPanel { Children = { action } }, id + "-actions", "다음 행동");
        var content = new StackPanel
        {
            Spacing = LauncherVisualTokens.SpaceMd,
            Children =
            {
                IconLabel(icon, Txt(title, LauncherVisualTokens.FontSection, true), LauncherVisualTokens.FontSection),
                Muted(detail, LauncherVisualTokens.FontBody),
                actions
            }
        };
        return Identify(Card(content), id, title);
    }

    private Control StartupSkeleton()
    {
        var brush = LauncherVisualTokens.Brush(LauncherVisualTokens.SkeletonColor(IsDeveloper, HighContrast));
        var blocks = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm };
        AutomationProperties.SetAccessibilityView(blocks, AccessibilityView.Raw);
        foreach (var (width, height) in new[] { (168d, 20d), (112d, 12d), (144d, 12d) })
        {
            blocks.Children.Add(new Border
            {
                Width = width * _preferences.TextScale,
                Height = height * _preferences.TextScale,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
                Background = brush
            });
        }
        return Identify(Card(blocks), "startup-skeleton", "프로그램 상태 확인 중");
    }
    private Control EnterpriseFilters()
    {
        var wrap=new WrapPanel {Orientation=Orientation.Horizontal};
        foreach(var control in new[]{ComboLine("환경",_config.Environment,["prod","dev"],v=>{_config.Environment=v;SelectionChanged();}),ComboLine("채널",_config.Channel,["stable","beta","dev"],v=>{_config.Channel=v;SelectionChanged();}),ComboLine("버전 정책",_config.VersionPolicy,["latest","exact"],v=>{_config.VersionPolicy=v;SelectionChanged();})})
        {control.Width=220;control.Margin=new Thickness(0,0,12,8);wrap.Children.Add(control);}
        if(_config.VersionPolicy=="exact"){var exact=ExactVersionLine();exact.Width=240;exact.Margin=new Thickness(0,0,0,8);wrap.Children.Add(exact);}
        return Card(wrap,12);
    }
    private Control EnterpriseOverview()
    {
        var narrow=CurrentLayoutWidth()<800||_preferences.TextScale>1.5;
        var grid=new Grid {ColumnDefinitions=new(narrow?"*,88":"*,164"),ColumnSpacing=LauncherVisualTokens.SpaceLg};
        var stack=new StackPanel {Spacing=LauncherVisualTokens.SpaceMd};
        stack.Children.Add(Txt(_selectedProject.DisplayName,LauncherVisualTokens.FontProject,true));
        var (tone, icon) = _viewModel.GeneralState switch
        {
            GeneralLauncherState.Ready => (LauncherStatusTone.Normal, LauncherIconKind.Check),
            GeneralLauncherState.UpdateAvailable or GeneralLauncherState.RuntimeBlocked or GeneralLauncherState.AwaitingPromotion =>
                (LauncherStatusTone.Caution, LauncherIconKind.Warning),
            GeneralLauncherState.RecoverableError or GeneralLauncherState.ConfigurationRequired =>
                (LauncherStatusTone.Error, LauncherIconKind.Error),
            GeneralLauncherState.NotInstalled => (LauncherStatusTone.Progress, LauncherIconKind.Package),
            _ => (LauncherStatusTone.Progress, LauncherIconKind.Refresh)
        };
        var status = Identify(Txt(_installState, LauncherVisualTokens.FontBody, true), "project-install-status", _installState);
        status.Foreground = StatusBrush(tone);
        stack.Children.Add(IconLabel(icon, status, LauncherVisualTokens.FontSection));
        if (IsDeveloper || _viewModel.GeneralState != GeneralLauncherState.Ready)
            stack.Children.Add(Identify(Muted(IsDeveloper ? _installDetail : GeneralStateDetail(),LauncherVisualTokens.FontBody),
                "project-state-guidance","프로그램 상태 안내"));
        if (!IsDeveloper && LowerRecommendation() is { } recommendation && _explainedRecommendation == recommendation)
            stack.Children.Add(Muted("현재 권장 버전을 설치할 수 있습니다. 이전 설치는 보존됩니다.", LauncherVisualTokens.FontBody));
        var versions=new WrapPanel {Orientation=Orientation.Horizontal};
        versions.Children.Add(new StackPanel {Margin=new Thickness(0,0,LauncherVisualTokens.Space2Xl,0),Children={Muted(!IsDeveloper && _viewModel.ProjectStatus is {IsInstalled:false,PreviousInstallation:not null}?"기존 설치 버전":"설치 버전",LauncherVisualTokens.FontCaption),Txt(ReadInstalledVersion()??"미설치",LauncherVisualTokens.FontBody,true)}});
        versions.Children.Add(new StackPanel {Children={Muted(IsDeveloper?"선택 배포 버전":"최신 배포 버전",LauncherVisualTokens.FontCaption),Txt(LatestCatalogVersion()??"확인 필요",LauncherVisualTokens.FontBody,true)}});
        stack.Children.Add(versions);grid.Children.Add(stack);
        var visual = narrow || HighContrast
            ? BrandedFallback(ProjectVisualResolver.Fallback(_selectedProject), hero: !narrow)
            : ProjectHeroVisual(ProjectVisualResolver.Resolve(_selectedProject, ConfigPath, ProjectVisualKind.Hero));
        visual.Height = narrow ? 72 : 108;
        visual.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAccessibilityView(visual, AccessibilityView.Raw);
        grid.Children.Add(At(visual, 1));
        return Card(grid,LauncherVisualTokens.SpaceLg);
    }
    private Control EnterpriseMaintenance()
    {
        var wrap=new WrapPanel {Orientation=Orientation.Horizontal};
        var buttons=new[]{EnterpriseButton("검증/복구","repair",async (_,_)=>await RunAsync(true,false),icon:LauncherIconKind.Wrench),EnterpriseButton("백업 복원","rollback",async (_,_)=>await RollbackLatestAsync(),icon:LauncherIconKind.Refresh),
            EnterpriseButton("임시 파일 정리","cache-clear",async(_,_)=>await RunMaintenanceAsync(false),icon:LauncherIconKind.Clear),EnterpriseButton("백업 정리","backup-cleanup",async(_,_)=>await RunMaintenanceAsync(true),icon:LauncherIconKind.Clear),
            EnterpriseButton("설치 폴더","open-folder",(_,_)=>OpenInstallFolder(),tracked:false,icon:LauncherIconKind.Folder),EnterpriseButton("로그 ZIP","export-logs",(_,_)=>ExportLogsZip(),tracked:false,icon:LauncherIconKind.Log)};
        foreach(var b in buttons){b.Margin=new Thickness(0,8,8,0);wrap.Children.Add(b);}return wrap;
    }
    private Control EnterpriseLogs()
    {
        _logBox=Identify(new TextBox {Text=_presentation.LogText,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=180,MaxHeight=360,FontSize=LauncherVisualTokens.FontCaption*_preferences.TextScale,FontFamily=new FontFamily("Cascadia Mono,Consolas"),Foreground=Fg(),Background=SurfaceBrush},"operation-log","작업 기록");
        return Card(new StackPanel {Spacing=LauncherVisualTokens.SpaceMd,Children={Txt("작업 기록",LauncherVisualTokens.FontSection,true),_logBox}});
    }
    private Control EnterpriseActionBar(bool projectCards = false)
    {
        var panel=new StackPanel {Spacing=LauncherVisualTokens.SpaceMd};var actions=new WrapPanel {Orientation=Orientation.Horizontal};
        var primaryIcon = _configurationError is not null || _readOnlyRecoveryFollowUp
            ? LauncherIconKind.Refresh
            : IsDeveloper ? LauncherIconKind.Play
            : _viewModel.PrimaryAction switch
            {
                PrimaryActionKind.Launch => LauncherIconKind.Play,
                PrimaryActionKind.InstallAndLaunch or PrimaryActionKind.UpdateAndLaunch => LauncherIconKind.Download,
                _ => LauncherIconKind.Refresh
            };
        var primary=EnterpriseButton(_configurationError is not null?"설정 다시 확인":_readOnlyRecoveryFollowUp?"상태 다시 확인":IsDeveloper?"실행":GeneralActionText(),"primary-action",async (_,_)=>{if(IsDeveloper && _configurationError is null)await RunAsync(false,true);else await ExecutePrimaryActionAsync();},true,icon:primaryIcon);
        primary.Tag="general-primary-action"; primary.IsEnabled=HasProject&&_viewModel.PrimaryAction!=PrimaryActionKind.Disabled&&!_running;
        if(IsDeveloper)primary.IsEnabled=HasProject&&!_running;
        primary.HotKey=new KeyGesture(Key.F5);
        primary.IsVisible = IsDeveloper || HasProject || _configurationError is not null || _showStartupSkeleton;
        if (!projectCards) actions.Children.Add(primary);
        if(IsDeveloper)actions.Children.Add(EnterpriseButton("업데이트","update",async (_,_)=>await RunAsync(false,false),icon:LauncherIconKind.Download));
        var check=EnterpriseButton(_readOnlyRecoveryFollowUp?"상태 다시 확인":_viewModel.GeneralState==GeneralLauncherState.RuntimeBlocked?"다시 확인":_viewModel.GeneralState==GeneralLauncherState.RecoverableError&&IsDeveloper?"문제 해결":"상태 확인","status-check",async (_,_)=>await ExecuteStatusActionAsync(),icon:LauncherIconKind.Check);
        if (!projectCards) { check.HotKey=new KeyGesture(Key.F6); actions.Children.Add(check); }
        if(_running && _operationCancellation is not null)
        {
            var cancel=EnterpriseButton(_operationCancellation.IsCancellationRequested?"취소 요청 중":"작업 취소","cancel-operation",(_,_)=>RequestOperationCancellation(),tracked:false,icon:LauncherIconKind.Stop);
            cancel.IsEnabled=!_operationCancellation.IsCancellationRequested && (IsDeveloper || !_presentation.Committing);actions.Children.Add(cancel);
            if (!IsDeveloper && _pauseAvailable && !_presentation.Committing && !_operationCancellation.IsCancellationRequested)
                actions.Children.Add(EnterpriseButton("일시정지", "pause-operation", (_, _) => RequestOperationCancellation(), tracked: false, icon: LauncherIconKind.Stop));
        }
        if(!_running && CanResumeSelected())actions.Children.Add(EnterpriseButton(IsDeveloper ? "다운로드 재개" : _retainedPartialDownload == true ? "계속" : "다시 시도","resume-operation",async(_,_)=>await ResumeUiOperationAsync(),icon:LauncherIconKind.Download));
        if(_viewModel.GeneralState!=GeneralLauncherState.RuntimeBlocked && _presentation.ErrorCode is not null && _presentation.Retry is not null)actions.Children.Add(EnterpriseButton("다시 시도","retry-operation",async (_,_)=>await RetryCurrentAsync(),icon:LauncherIconKind.Refresh));
        foreach(var item in actions.Children)item.Margin=new Thickness(0,0,LauncherVisualTokens.SpaceSm,0);
        panel.Children.Add(actions);
        _statusText=Identify(Txt(_presentation.Title,LauncherVisualTokens.FontBody,true),"operation-status","작업 상태");
        _percentText=Muted("",LauncherVisualTokens.FontSecondary);_stageLabel=Muted("",LauncherVisualTokens.FontSecondary);
        _progress=Identify(new ProgressBar {Minimum=0,Maximum=100,Height=6,
            Background=LauncherVisualTokens.Brush(LauncherVisualTokens.MutedSurfaceColor(IsDeveloper, HighContrast)),
            Foreground=StatusBrush(LauncherStatusTone.Progress)},"operation-progress","작업 진행률");
        if(_announcer?.Parent is Panel oldParent)oldParent.Children.Remove(_announcer);
        _announcer ??= Identify(new TextBlock {Height=1,Opacity=0,IsHitTestVisible=false},"operation-announcement","작업 상태 알림");
        AutomationProperties.SetLiveSetting(_announcer,AutomationLiveSetting.Polite);
        // Bound the title together with the details: two large-text lines must not
        // consume the remaining project workspace in a short window.
        var detailsPanel=new StackPanel {Spacing=8};detailsPanel.Children.Add(_statusText);var detail=new Grid {ColumnDefinitions=new("*,Auto")};detail.Children.Add(_stageLabel);detail.Children.Add(At(_percentText,1));detailsPanel.Children.Add(detail);detailsPanel.Children.Add(_progress);panel.Children.Add(_announcer);
        if (!IsDeveloper && _presentation.InstallationNotice is { } notice)
            detailsPanel.Children.Add(Identify(Txt(notice, LauncherVisualTokens.FontBody, true), "installation-completed", notice));
        if (!IsDeveloper && _viewModel.GeneralState == GeneralLauncherState.Ready)
            detailsPanel.Children.Add(Identify(Muted(GeneralStateDetail(), LauncherVisualTokens.FontBody), "project-state-guidance", "프로그램 상태 안내"));
        if (!IsDeveloper && _running && _presentation.Committing)
            detailsPanel.Children.Add(Identify(Muted("설치 마무리 중에는 멈출 수 없습니다.", 14), "commit-guidance", "설치 마무리 안내"));
        _downloadCancellationGuidance = null;
        if (!IsDeveloper && _running && _operationCancellation is not null && !_presentation.Committing)
        {
            _downloadCancellationGuidance = Identify(Muted("취소하면 다음 시도에서 다운로드를 처음부터 받을 수 있습니다.", 14), "cancel-guidance", "다운로드 취소 안내");
            detailsPanel.Children.Add(_downloadCancellationGuidance);
        }
        if(CurrentLayoutWidth()<800 && !projectCards && _serviceLabel is not null)detailsPanel.Children.Add(_serviceLabel);
        if(_presentation.SupportId is not null)detailsPanel.Children.Add(Muted("지원 ID: "+_presentation.SupportId,12));
        _actionDetails=Identify(new ScrollViewer {Content=detailsPanel,Focusable=true,IsTabStop=true,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},"action-details","작업 상세 안내");
        panel.Children.Add(_actionDetails);
        return Card(panel,12);
    }
    private void UpdateActionDetailsLimit()
    {
        if(_actionDetails is not null)_actionDetails.MaxHeight=Math.Max(48,Math.Min(140,ClientSize.Height*0.18));
    }
    private Control? FindFocusTarget(string? id)
    {
        var controls=this.GetVisualDescendants().OfType<Control>().Where(c=>c.IsEffectivelyVisible&&c.IsEnabled&&c.Focusable).ToList();
        foreach(var candidate in new[]{id,"project-list","compact-project","status-check","card-refresh","settings"})
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
        var value=System.Text.Json.JsonSerializer.Serialize(new {profile=_config.ClientProfile,sessionId=_displaySessionId,
            sessionStartedUtc=_displaySessionStarted,processId=Environment.ProcessId,screenPixels=screen?.Bounds.ToString(),
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
        var active = _running && _presentation.Kind != LauncherUiOperation.None && _presentation.ErrorCode is null;
        var showProgress = IsDeveloper || active || _presentation.ShowCompletion;
        if(_statusText is not null)_statusText.Text=_presentation.Title;
        if (_downloadCancellationGuidance is not null)
            _downloadCancellationGuidance.IsVisible = active && !_pauseAvailable && _presentation.CurrentStage == "파일 준비";
        var percent = IsDeveloper || _presentation.ShowCompletion || _presentation.Percent is null
            ? _presentation.Percent : Math.Min(99, _presentation.Percent.Value);
        if(_percentText is not null){_percentText.IsVisible=showProgress;_percentText.Text=percent is { } p?$"{p:0}%":_running?"진행 중":"대기";}
        if(_progress is not null){_progress.IsVisible=showProgress;_progress.IsIndeterminate=percent is null && _running;_progress.Value=percent??0;}
        if(_stageLabel is not null)
        {
            _stageLabel.IsVisible=showProgress;
            _stageLabel.Text=!IsDeveloper && _operationCancellation is not null && _presentation.Kind != LauncherUiOperation.Check
                ? _presentation.DownloadSteps : "현재 단계 · "+_presentation.CurrentStage;
        }
        if(_announcer is not null && _announcer.Text!=_presentation.Announcement)_announcer.Text=_presentation.Announcement;
    }
}
