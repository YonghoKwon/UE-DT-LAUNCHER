using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private sealed record ProjectCardStatus(LauncherUiOperationContext Context, GeneralLauncherState State,
        string Label, string Action, string? Guidance = null);

    private readonly Dictionary<string, ProjectCardStatus> _projectCardStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap?> _projectCardImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _projectCardLifetime = new();
    private bool _hasAuthorizedCatalog;
    private bool _showProjectDetails;
    private bool _cardOperation;
    private string? _projectListNotice;
    private string? _workspaceFocus;

    private bool HasProjectCards => !IsDeveloper && _hasAuthorizedCatalog && VisibleProjectCount() > 1;
    private bool ShowsProjectCards => HasProjectCards && !_showProjectDetails;

    private void ResetProjectCards()
    {
        _hasAuthorizedCatalog = _showProjectDetails = false;
        _projectCardStatuses.Clear();
        _projectCardImages.Clear();
        _projectListNotice = null;
    }

    private void UpdateProjectCardCatalog(HashSet<string> previous, string previousSelection)
    {
        var allowed = _viewModel.ProjectsForProfile().Select(project => project.ProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _projectListNotice = _hasAuthorizedCatalog && previous.Except(allowed, StringComparer.OrdinalIgnoreCase).Any()
            ? "받을 권한이 변경된 프로그램을 목록에서 제외했습니다."
            : null;
        if (!allowed.Contains(previousSelection)) _showProjectDetails = false;
        _hasAuthorizedCatalog = true;
        _projectCardStatuses.Clear();
        _projectCardImages.Clear();
    }

    private async Task RefreshProjectCardsAsync()
    {
        _running = true;
        Build();
        try
        {
            foreach (var project in _viewModel.ProjectsForProfile().ToList())
            {
                var token = _projectCardLifetime.Token;
                var context = DisplaySelectionContext(project);
                _projectCardImages[project.ProjectId] = await LoadProjectCardImageAsync(project);
                if (token.IsCancellationRequested) break;
                _projectCardStatuses[project.ProjectId] = await ReadProjectCardStatusAsync(project, context, token);
                if (token.IsCancellationRequested) break;
                Build();
            }
        }
        catch (OperationCanceledException) when (_projectCardLifetime.IsCancellationRequested) { }
        finally
        {
            _running = false;
            if (!_projectCardLifetime.IsCancellationRequested) Build();
        }
    }

    private async Task<Bitmap?> LoadProjectCardImageAsync(ProjectUiConfig project)
    {
        var configPath = ConfigPath;
        var asset = await Task.Run(() => ProjectVisualResolver.Resolve(project, configPath, ProjectVisualKind.Thumbnail));
        if (asset.ResolvedPath is not { } path || _projectCardLifetime.IsCancellationRequested) return null;
        if (_projectVisualCache.TryGetValue(path, out var cached)) return cached;
        var bitmap = await Task.Run(() =>
        {
            try { return new Bitmap(path); }
            catch { return null; }
        });
        if (bitmap is null) return null;
        if (_projectCardLifetime.IsCancellationRequested) { bitmap.Dispose(); return null; }
        if (_projectVisualCache.TryGetValue(path, out cached)) { bitmap.Dispose(); return cached; }
        _projectVisualCache.Add(path, bitmap);
        return bitmap;
    }

    private async Task<ProjectCardStatus> ReadProjectCardStatusAsync(ProjectUiConfig project,
        LauncherUiOperationContext context, CancellationToken token)
    {
        try
        {
            if (context.Selection is null)
            {
                if (_catalog.SelectionPolicy == CatalogRecommendation.ExplicitPolicy && context.VersionPolicy != "exact")
                    return new(context, GeneralLauncherState.AwaitingPromotion, "실행 버전 지정 대기", "다시 확인");
                throw new InvalidOperationException("허용된 배포 버전을 먼저 선택해 주세요.");
            }

            var config = await RunProjectConfig(project, context);
            var result = await _uiBackend.CheckAsync(context, config, _ => { }, token);
            context.Validate(result);
            var runtime = LauncherDashboardViewModel.RequireRuntimeObservation(result.Runtime);
            if (runtime.State != RuntimeState.Quiescent)
                return new(context, GeneralLauncherState.RuntimeBlocked,
                    LauncherDashboardViewModel.RuntimeLabel(runtime.State), "다시 확인");

            var state = LauncherDashboardViewModel.InstallationState(result.Status, developer: false);
            return new(context, state, LauncherDashboardViewModel.InstallationLabel(state),
                CardAction(state, result.Status, LowerRecommendation(context.VersionPolicy,
                    SelectedCatalogRelease(project.ProjectId), result.Status) is not null));
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            if (error.GetBaseException() is RuntimeBlockedException blocked)
                return new(context, GeneralLauncherState.RuntimeBlocked,
                    LauncherDashboardViewModel.RuntimeLabel(blocked.Observation.State), "다시 확인");
            return new(context, GeneralLauncherState.RecoverableError, "오류", "다시 확인", LauncherUiError.From(error).Message);
        }
    }

    private static string CardAction(GeneralLauncherState state, ManagedProjectStatus? status, bool lowerRecommendation) =>
        state switch
        {
            GeneralLauncherState.NotInstalled => "설치",
            GeneralLauncherState.Ready => "실행",
            GeneralLauncherState.UpdateAvailable when IsInstallationDamaged(status) => "문제 해결",
            GeneralLauncherState.UpdateAvailable when lowerRecommendation => "권장 버전 변경 안내",
            GeneralLauncherState.UpdateAvailable => "업데이트",
            _ => "다시 확인"
        };

    private void RecordSelectedCardStatus()
    {
        if (!HasProjectCards || !HasProject) return;
        var state = _viewModel.GeneralState;
        _projectCardStatuses[_selectedProject.ProjectId] = new(DisplaySelectionContext(), state, _installState,
            _readOnlyRecoveryFollowUp ? "다시 확인" : CardAction(state, _viewModel.ProjectStatus, LowerRecommendation() is not null),
            state == GeneralLauncherState.RecoverableError ? _installDetail : null);
    }

    private void SelectCardProject(ProjectUiConfig project)
    {
        if (string.Equals(project.ProjectId, _selectedProject.ProjectId, StringComparison.OrdinalIgnoreCase)) return;
        _selectedProject = project;
        _config.ProjectId = project.ProjectId;
        InvalidateDisplayedInstallation();
        _presentation.Begin(LauncherUiOperation.None);
        UpdateReleaseNotes();
    }

    private async Task OpenProjectCardAsync(ProjectUiConfig project)
    {
        if (_running || !ShowsProjectCards) return;
        SelectCardProject(project);
        _showProjectDetails = true;
        await RefreshInstallStatusAsync(suppressDialog: true);
        _workspaceFocus = "project-cards-back";
        Build();
    }

    private async Task ExecuteProjectCardAsync(ProjectUiConfig project, ProjectCardStatus status)
    {
        if (_running || !ShowsProjectCards || status.Context != DisplaySelectionContext(project)) return;
        if (status.Action == "다시 확인")
        {
            await RefreshSelectionStatusAsync();
            return;
        }
        if (!_projectCardStatuses.TryGetValue(project.ProjectId, out var current) || current != status) return;
        if (status.Action == "권장 버전 변경 안내")
        {
            await OpenProjectCardAsync(project);
            return;
        }

        SelectCardProject(project);
        _cardOperation = true;
        try
        {
            // Recheck the exact selection before turning a previously displayed action into work.
            await RefreshInstallStatusAsync(suppressDialog: true);
            if (!_projectCardStatuses.TryGetValue(project.ProjectId, out current) ||
                current.Context != status.Context || current.State != status.State || current.Action != status.Action)
                return;
            await ExecutePrimaryActionAsync();
        }
        finally
        {
            _cardOperation = false;
            _workspaceFocus = "card-open-" + project.ProjectId;
            Build();
        }
    }

    private Control ProjectCards()
    {
        var heading = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = LauncherVisualTokens.SpaceMd };
        heading.Children.Add(Txt("내 프로그램", LauncherVisualTokens.FontProject, true));
        var search = Identify(new TextBox
        {
            Text = _search, Watermark = "프로젝트 검색", MinWidth = 160,
            FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale
        }, "card-search", "프로젝트 검색");
        heading.Children.Add(At(search, 1));
        var refresh = EnterpriseButton("새로고침", "card-refresh", async (_, _) => await RefreshSelectionStatusAsync(), icon: LauncherIconKind.Refresh);
        refresh.HotKey = new KeyGesture(Key.F6);
        heading.Children.Add(At(refresh, 2));

        var panel = new StackPanel { Spacing = LauncherVisualTokens.SpaceMd, Children = { heading } };
        if (_projectListNotice is { } notice)
            panel.Children.Add(ProjectCardNotice(notice));
        if (_catalogState == "카탈로그 오류")
            panel.Children.Add(ProjectCardNotice("프로그램 목록을 확인할 수 없습니다. 다시 확인해 주세요.", "card-catalog-error"));
        if (_running)
            panel.Children.Add(ProjectCardNotice(_cardOperation
                ? $"{_selectedProject.DisplayName} 작업이 끝나면 다른 프로그램을 사용할 수 있습니다."
                : "프로그램 상태를 확인하고 있습니다.", "card-busy-guidance"));

        var columns = CurrentLayoutWidth() < 800 || _preferences.TextScale > 1.5 ? 2 : 3;
        var cards = Identify(new WrapPanel(), "project-cards", "프로그램 카드 목록");
        var empty = Identify(Muted("검색 결과가 없습니다.", LauncherVisualTokens.FontBody), "card-search-empty", "검색 결과가 없습니다.");
        var openButtons = new List<Button>();
        void Render()
        {
            cards.Children.Clear();
            openButtons.Clear();
            foreach (var project in VisibleProjects())
                cards.Children.Add(ProjectWorkspaceCard(project, columns, openButtons));
            empty.IsVisible = openButtons.Count == 0;
        }
        Render();
        search.TextChanged += (_, _) => { _search = search.Text ?? string.Empty; Render(); };
        panel.Children.Add(cards);
        panel.Children.Add(empty);
        return panel;
    }

    private Control ProjectCardNotice(string notice, string id = "card-permission-notice")
    {
        var guidance = Identify(Muted(notice, LauncherVisualTokens.FontBody), id, notice);
        AutomationProperties.SetLiveSetting(guidance, AutomationLiveSetting.Polite);
        return guidance;
    }

    private Control ProjectWorkspaceCard(ProjectUiConfig project, int columns, List<Button> openButtons)
    {
        _projectCardStatuses.TryGetValue(project.ProjectId, out var status);
        if (_catalogState == "카탈로그 오류")
            status = new(DisplaySelectionContext(project), GeneralLauncherState.RecoverableError, "확인 필요", "다시 확인");
        var active = _cardOperation && _running && _selectedProject.ProjectId == project.ProjectId;
        var label = active ? "작업 중" : status?.Label ?? "상태 확인 중";
        var name = $"{project.DisplayName} — {label}";
        var visual = _projectCardImages.GetValueOrDefault(project.ProjectId) is { } bitmap && !HighContrast
            ? new Border { ClipToBounds = true, CornerRadius = new(LauncherVisualTokens.RadiusControl),
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill } }
            : BrandedFallback(ProjectVisualResolver.Fallback(project), hero: false);
        visual.Height = 92;
        var text = Txt(label, LauncherVisualTokens.FontCaption, true);
        text.Foreground = StatusBrush(status?.State switch
        {
            GeneralLauncherState.Ready => LauncherStatusTone.Normal,
            GeneralLauncherState.RecoverableError => LauncherStatusTone.Error,
            GeneralLauncherState.RuntimeBlocked or GeneralLauncherState.UpdateAvailable => LauncherStatusTone.Caution,
            _ => LauncherStatusTone.Progress
        });
        var face = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm,
            Children = { visual, Txt(project.DisplayName, LauncherVisualTokens.FontSection, true), text } };
        if (status?.Guidance is { } guidance) face.Children.Add(Muted(guidance, LauncherVisualTokens.FontCaption));
        if (status is null)
            face.Children.Add(new Border { Height = 6, Width = 96, HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new(3), Background = LauncherVisualTokens.Brush(LauncherVisualTokens.MutedSurfaceColor(false, HighContrast)) });
        var open = EnterpriseButton(name, "card-open-" + project.ProjectId, async (_, _) => await OpenProjectCardAsync(project), tracked: false);
        open.Content = face;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        open.IsEnabled = !_running && _catalogState != "카탈로그 오류";
        openButtons.Add(open);
        open.KeyDown += (_, args) =>
        {
            var direction = args.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -columns, Key.Down => columns, _ => 0 };
            if (direction == 0) return;
            var index = Math.Clamp(openButtons.IndexOf(open) + direction, 0, openButtons.Count - 1);
            openButtons[index].Focus(NavigationMethod.Directional);
            args.Handled = true;
        };
        var actionText = status?.Action ?? "상태 확인 중";
        var action = EnterpriseButton(actionText, "card-action-" + project.ProjectId, async (_, _) =>
        {
            if (status is not null) await ExecuteProjectCardAsync(project, status);
        }, primary: true, tracked: false);
        action.IsEnabled = !_running && status is not null && status.State != GeneralLauncherState.RuntimeBlocked;
        if (status?.State == GeneralLauncherState.RuntimeBlocked)
            ToolTip.SetTip(action, "프로그램을 종료하거나 실행 상태를 확인한 뒤 새로고침하세요.");
        var card = Card(new StackPanel { Spacing = LauncherVisualTokens.SpaceMd, Children = { open, action } });
        card.Width = Math.Max(216, Math.Floor((CurrentLayoutWidth() - 90) / columns));
        card.Margin = new Thickness(0, 0, LauncherVisualTokens.SpaceMd, LauncherVisualTokens.SpaceMd);
        if (_selectedProject.ProjectId == project.ProjectId)
            card.BorderBrush = LauncherVisualTokens.Brush(LauncherVisualTokens.ProjectSelectionColor(false, HighContrast));
        return Identify(card, "program-card-" + project.ProjectId, name);
    }

    private Control ProjectCardsBack() => EnterpriseButton("뒤로", "project-cards-back", (_, _) =>
    {
        if (_running) return;
        _showProjectDetails = false;
        _workspaceFocus = "card-open-" + _selectedProject.ProjectId;
        Build();
    });
}
