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

    private sealed record ProjectCardsView(WrapPanel Cards, TextBlock Empty, TextBlock Notice, TextBlock Error, TextBlock Busy)
    {
        internal Dictionary<string, ProjectCardView> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ProjectCardView(Border Card, Button Open, Button Action, StackPanel Face,
        TextBlock Title, TextBlock Label, TextBlock Guidance, Border Skeleton, TextBlock ActionText)
    {
        internal Bitmap? Bitmap { get; set; }
        internal ProjectCardStatus? Status { get; set; }
        internal bool Selected { get; set; }
    }

    private readonly Dictionary<string, ProjectCardStatus> _projectCardStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Path, Bitmap? Bitmap)> _projectCardImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _projectCardLifetime = new();
    private ProjectCardsView? _projectCardsView;
    private bool _hasAuthorizedCatalog;
    private bool _showProjectDetails;
    private bool _cardOperation;
    private string? _projectListNotice;
    private string? _workspaceFocus;

    private bool HasProjectCards => !IsDeveloper && _hasAuthorizedCatalog &&
        _viewModel.ProjectsForProfile().Select(project => project.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1;
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
        var allowed = _viewModel.ProjectsForProfile().DistinctBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(project => project.ProjectId, StringComparer.OrdinalIgnoreCase);
        _projectListNotice = _hasAuthorizedCatalog && previous.Except(allowed.Keys, StringComparer.OrdinalIgnoreCase).Any()
            ? "받을 수 있는 프로그램 목록이 변경되었습니다."
            : null;
        if (!allowed.ContainsKey(previousSelection)) _showProjectDetails = false;
        _hasAuthorizedCatalog = true;
        foreach (var (id, status) in _projectCardStatuses)
            if (!allowed.TryGetValue(id, out var project) || status.Context != DisplaySelectionContext(project))
                _projectCardStatuses.Remove(id);
        foreach (var (id, image) in _projectCardImages)
            if (!allowed.TryGetValue(id, out var project) || image.Path != project.ThumbnailPath)
                _projectCardImages.Remove(id);
    }

    private async Task RefreshProjectCardsAsync(bool pendingOnly = false)
    {
        var projects = _viewModel.ProjectsForProfile()
            .DistinctBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Where(project => !pendingOnly || !_projectCardStatuses.ContainsKey(project.ProjectId) ||
                !_projectCardImages.ContainsKey(project.ProjectId))
            .ToList();
        if (projects.Count == 0) return;
        _running = true;
        if (_projectCardsView is null) Build();
        else SyncProjectCards(_projectCardsView);
        SetBusy(true);
        try
        {
            foreach (var project in projects)
            {
                var id = project.ProjectId;
                var token = _projectCardLifetime.Token;
                var context = DisplaySelectionContext(project);
                var bitmap = _projectCardImages.TryGetValue(id, out var image) && image.Bitmap is not null
                    ? image.Bitmap : await LoadProjectCardImageAsync(project);
                if (token.IsCancellationRequested) break;
                var status = await ReadProjectCardStatusAsync(project, context, token);
                if (token.IsCancellationRequested) break;
                var changed = !_projectCardStatuses.TryGetValue(id, out var previous) || previous != status ||
                    _projectCardImages.GetValueOrDefault(id).Bitmap != bitmap;
                _projectCardImages[id] = (project.ThumbnailPath, bitmap);
                _projectCardStatuses[id] = status;
                if (changed && _projectCardsView?.Items.TryGetValue(id, out var card) == true)
                    UpdateProjectCard(project, card);
            }
        }
        catch (OperationCanceledException) when (_projectCardLifetime.IsCancellationRequested) { }
        finally
        {
            _running = false;
            if (!_projectCardLifetime.IsCancellationRequested) SetBusy(false);
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
        var columns = CurrentLayoutWidth() < 800 || _preferences.TextScale > 1.5 ? 2 : 3;
        var cardWidth = Math.Max(216, Math.Floor((CurrentLayoutWidth() - 90) / columns));
        var view = new ProjectCardsView(
            Identify(new WrapPanel { ItemWidth = cardWidth + LauncherVisualTokens.SpaceMd }, "project-cards", "프로그램 카드 목록"),
            Identify(Muted("검색 결과가 없습니다.", LauncherVisualTokens.FontBody), "card-search-empty", "검색 결과가 없습니다."),
            ProjectCardNotice(_projectListNotice ?? string.Empty),
            ProjectCardNotice("프로그램 목록을 확인할 수 없습니다. 다시 확인해 주세요.", "card-catalog-error"),
            ProjectCardNotice(string.Empty, "card-busy-guidance"));
        _projectCardsView = view;
        panel.Children.AddRange([view.Notice, view.Error, view.Busy, view.Cards, view.Empty]);
        SyncProjectCards(view);
        search.TextChanged += (_, _) =>
        {
            _search = search.Text ?? string.Empty;
            SyncProjectCards(view);
        };
        return panel;
    }

    private TextBlock ProjectCardNotice(string notice, string id = "card-permission-notice")
    {
        var guidance = Identify(Muted(notice, LauncherVisualTokens.FontBody), id, notice);
        AutomationProperties.SetLiveSetting(guidance, AutomationLiveSetting.Polite);
        return guidance;
    }

    private void SyncProjectCards(ProjectCardsView view)
    {
        var allowed = _viewModel.ProjectsForProfile().Select(project => project.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in view.Items.Keys)
            if (!allowed.Contains(id)) view.Items.Remove(id);
        var cards = new List<Control>();
        foreach (var project in VisibleProjects().DistinctBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase))
        {
            if (!view.Items.TryGetValue(project.ProjectId, out var card))
                view.Items.Add(project.ProjectId, card = CreateProjectCard(project, view.Cards));
            UpdateProjectCard(project, card);
            cards.Add(card.Card);
        }
        if (!view.Cards.Children.SequenceEqual(cards))
        {
            view.Cards.Children.Clear();
            view.Cards.Children.AddRange(cards);
        }
        view.Empty.IsVisible = cards.Count == 0;
        view.Notice.IsVisible = _projectListNotice is not null;
        view.Notice.Text = _projectListNotice;
        AutomationProperties.SetName(view.Notice, _projectListNotice);
        view.Error.IsVisible = _catalogState == "카탈로그 오류";
    }

    private void UpdateProjectCardsAvailability()
    {
        if (_projectCardsView is not { } view) return;
        view.Busy.IsVisible = _running;
        view.Busy.Text = _cardOperation
            ? $"{_selectedProject.DisplayName} 작업이 끝나면 다른 프로그램을 사용할 수 있습니다."
            : "프로그램 상태를 확인하고 있습니다.";
        AutomationProperties.SetName(view.Busy, view.Busy.Text);
        foreach (var (id, card) in view.Items)
            SetProjectCardAvailability(card, _projectCardStatuses.GetValueOrDefault(id));
    }

    private ProjectCardStatus? DisplayedCardStatus(ProjectUiConfig project)
    {
        if (_catalogState == "카탈로그 오류")
            return new(DisplaySelectionContext(project), GeneralLauncherState.RecoverableError, "확인 필요", "다시 확인");
        return _projectCardStatuses.GetValueOrDefault(project.ProjectId);
    }

    private void SetProjectCardAvailability(ProjectCardView card, ProjectCardStatus? status)
    {
        var catalogError = _catalogState == "카탈로그 오류";
        card.Open.IsEnabled = !_running && !catalogError;
        card.Action.IsEnabled = !_running && (catalogError || status is not null && status.State != GeneralLauncherState.RuntimeBlocked);
    }

    private void UpdateProjectCard(ProjectUiConfig project, ProjectCardView card)
    {
        var status = DisplayedCardStatus(project);
        var active = _cardOperation && _running && _selectedProject.ProjectId == project.ProjectId;
        var label = active ? "작업 중" : status?.Label ?? "상태 확인 중";
        var name = $"{project.DisplayName} — {label}";
        var bitmap = HighContrast ? null : _projectCardImages.GetValueOrDefault(project.ProjectId).Bitmap;
        var selected = _selectedProject.ProjectId == project.ProjectId;
        if (card.Status == status && card.Title.Text == project.DisplayName && card.Label.Text == label &&
            card.Bitmap == bitmap && card.Selected == selected) return;
        if (card.Title.Text != project.DisplayName || card.Bitmap != bitmap)
        {
            var visual = bitmap is not null
                ? new Border { ClipToBounds = true, CornerRadius = new(LauncherVisualTokens.RadiusControl),
                    Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill } }
                : BrandedFallback(ProjectVisualResolver.Fallback(project), hero: false);
            visual.Height = 92;
            card.Face.Children[0] = visual;
            card.Bitmap = bitmap;
        }
        card.Title.Text = project.DisplayName;
        card.Status = status;
        card.Selected = selected;
        card.Label.Text = label;
        card.Label.Foreground = StatusBrush(status?.State switch
        {
            GeneralLauncherState.Ready => LauncherStatusTone.Normal,
            GeneralLauncherState.RecoverableError => LauncherStatusTone.Error,
            GeneralLauncherState.RuntimeBlocked or GeneralLauncherState.UpdateAvailable => LauncherStatusTone.Caution,
            _ => LauncherStatusTone.Progress
        });
        card.Guidance.Text = status?.Guidance;
        card.Guidance.IsVisible = status?.Guidance is not null;
        card.Skeleton.IsVisible = status is null;
        card.ActionText.Text = status?.Action ?? "상태 확인 중";
        AutomationProperties.SetName(card.Card, name);
        AutomationProperties.SetName(card.Open, name);
        AutomationProperties.SetName(card.Action, card.ActionText.Text);
        ToolTip.SetTip(card.Action, status?.State == GeneralLauncherState.RuntimeBlocked
            ? "프로그램을 종료하거나 실행 상태를 확인한 뒤 새로고침하세요." : null);
        card.Card.BorderBrush = LauncherVisualTokens.Brush(selected
            ? LauncherVisualTokens.ProjectSelectionColor(false, HighContrast)
            : LauncherVisualTokens.BorderColor(false, HighContrast));
        SetProjectCardAvailability(card, status);
    }

    private ProjectCardView CreateProjectCard(ProjectUiConfig project, WrapPanel panel)
    {
        var visual = BrandedFallback(ProjectVisualResolver.Fallback(project), hero: false);
        visual.Height = 92;
        var title = Txt(project.DisplayName, LauncherVisualTokens.FontSection, true);
        var label = Txt(string.Empty, LauncherVisualTokens.FontCaption, true);
        var guidance = Muted(string.Empty, LauncherVisualTokens.FontCaption);
        var skeleton = new Border { Height = 6, Width = 96, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new(3), Background = LauncherVisualTokens.Brush(LauncherVisualTokens.MutedSurfaceColor(false, HighContrast)) };
        var face = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm,
            Children = { visual, title, label, guidance, skeleton } };
        var open = EnterpriseButton(project.DisplayName, "card-open-" + project.ProjectId,
            async (_, _) => await OpenProjectCardAsync(project), tracked: false);
        open.Content = face;
        open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var action = EnterpriseButton(string.Empty, "card-action-" + project.ProjectId, async (_, _) =>
        {
            if (DisplayedCardStatus(project) is { } status) await ExecuteProjectCardAsync(project, status);
        }, primary: true, tracked: false);
        var card = Card(new StackPanel { Spacing = LauncherVisualTokens.SpaceMd, Children = { open, action } });
        card.Margin = new Thickness(0, 0, LauncherVisualTokens.SpaceMd, LauncherVisualTokens.SpaceMd);
        open.KeyDown += (_, args) => MoveProjectCardFocus(panel, card, args);
        Identify(card, "program-card-" + project.ProjectId, project.DisplayName);
        return new(card, open, action, face, title, label, guidance, skeleton, (TextBlock)action.Content!);
    }

    private static void MoveProjectCardFocus(WrapPanel panel, Border card, KeyEventArgs args)
    {
        Control? target;
        if (args.Key is Key.Left or Key.Right)
        {
            var index = panel.Children.IndexOf(card) + (args.Key == Key.Left ? -1 : 1);
            target = index >= 0 && index < panel.Children.Count ? panel.Children[index] : null;
        }
        else if (args.Key is Key.Up or Key.Down)
        {
            var direction = args.Key == Key.Up ? -1 : 1;
            target = panel.Children
                .Where(candidate => direction * (candidate.Bounds.Top - card.Bounds.Top) > 1)
                .OrderBy(candidate => direction * (candidate.Bounds.Top - card.Bounds.Top))
                .ThenBy(candidate => Math.Abs(candidate.Bounds.Center.X - card.Bounds.Center.X))
                .FirstOrDefault();
        }
        else return;
        if (target is Border { Child: StackPanel body } && body.Children[0] is Button open)
            open.Focus(NavigationMethod.Directional);
        args.Handled = true;
    }

    private Control ProjectCardsBack() => EnterpriseButton("뒤로", "project-cards-back", async (_, _) =>
    {
        if (_running) return;
        _showProjectDetails = false;
        _workspaceFocus = "card-open-" + _selectedProject.ProjectId;
        Build();
        await RefreshProjectCardsAsync(pendingOnly: true);
    });
}
