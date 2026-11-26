using Avalonia.Controls;

namespace UeDtLauncher.Gui;

internal sealed record TrayProjectStatus(string Name, string Status, bool HasUpdate = false);
internal sealed record LauncherTrayStatus(IReadOnlyList<TrayProjectStatus> Projects, bool ConnectionFailed = false, bool CheckFailed = false)
{
    internal bool HasUpdate => Projects.Any(project => project.HasUpdate);
}

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _trayLifetime = new();
    private HashSet<string> _trayHiddenProjects = new(StringComparer.OrdinalIgnoreCase);
    private Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>>? _trayCatalogLoader;
    private TimeProvider _trayClock = TimeProvider.System;
    private long? _lastTrayCheckAt;
    private (string Environment, string Channel)? _trayTrack;
    private bool _startupStarted;
    private bool _windowClosed;
    private bool _exitingFromTray;
    private Window? _trayNotice;
    private Task _trayNoticeSave = Task.CompletedTask;
    internal LauncherTrayStatus TrayStatus { get; private set; } = new([]);
    internal event Action? TrayStatusChanged;
    internal bool StatusTimerRunning => _agentStatusTimer.IsEnabled;
    internal TimeSpan StatusTimerInterval => _agentStatusTimer.Interval;
    internal bool TrayCheckAvailable => !_running && !_windowClosed && _configurationError is null;
    internal Task TrayNoticeSaved => _trayNoticeSave;

    internal void EnableTray(Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>> loadCatalog,
        TimeProvider? clock = null)
    {
        _trayCatalogLoader = loadCatalog;
        _trayClock = clock ?? TimeProvider.System;
        Closing += HideToTrayOnClose;
        StartStatusPolling();
    }

    private void ResetTrayStatus()
    {
        _lastTrayCheckAt = null;
        _trayTrack = null;
        var visible = _viewModel.ProjectsForProfile().Select(project => project.ProjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _trayHiddenProjects = _config.Projects.Select(project => project.ProjectId).Where(id => !visible.Contains(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetTrayStatus(new([]));
    }

    private void ClearTrayStatusForTrackChange()
    {
        if (_trayTrack is not { } track || track == (_config.Environment, _config.Channel)) return;
        _trayTrack = null;
        _lastTrayCheckAt = null;
        SetTrayStatus(new([]));
        if (!_config.IsManagedDeployment && _agentStatusTimer.IsEnabled) StartStatusPolling();
    }

    private void HideToTrayOnClose(object? sender, WindowClosingEventArgs args)
    {
        if (_exitingFromTray || !_preferences.KeepInTray || args.CloseReason != WindowCloseReason.WindowClosing)
            return;

        args.Cancel = true;
        // Closing dialogs completes pending confirmations with their default result.
        foreach (var dialog in _openDialogs.ToArray().Reverse())
            if (dialog != _trayNotice && dialog.IsVisible) dialog.Close();
        Hide();
        if (!_preferences.HasSeenTrayNotice)
            ShowTrayNotice();
    }

    private void ShowTrayNotice()
    {
        const string title = "알림 영역에 계속 실행 중";
        const string message = "런처는 알림 영역에서 계속 실행됩니다. 종료하려면 아이콘 메뉴의 [종료]를 누르세요.";
        var close = Identify(PrimaryButton("확인", (_, _) => _trayNotice?.Close(), 40), "tray-notice-close", "확인");
        var notice = AccessibleDialog(title, Txt(message, LauncherVisualTokens.FontBody, false), close);
        notice.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        notice.Closed += (_, _) => _trayNotice = null;
        _trayNotice = notice;
        notice.Show();
        _preferences = _preferences with { HasSeenTrayNotice = true };
        _trayNoticeSave = SaveTrayNoticeAsync();
    }

    private async Task SaveTrayNoticeAsync()
    {
        try
        {
            await LauncherUiPreferences.UpdateAsync(_preferencesPath, latest => latest with { HasSeenTrayNotice = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("알림 영역 안내를 본 기록을 저장하지 못했습니다.", force: true);
        }
    }

    internal void OpenFromTray()
    {
        if (_windowClosed || _exitingFromTray) return;
        _trayNotice?.Close();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal async Task ExitFromTrayAsync()
    {
        if (_windowClosed || _exitingFromTray) return;
        _exitingFromTray = true;
        await _trayNoticeSave;
        if (!_windowClosed) Close();
    }

    private void StartStatusPolling()
    {
        if (!_allowAutomaticChecks || _windowClosed || _configurationError is not null ||
            (!_config.IsManagedDeployment && _trayCatalogLoader is null)) return;

        _agentStatusTimer.Stop();
        _agentStatusTimer.Interval = _config.IsManagedDeployment
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(Math.Max(3, TimeUntilTrayCheck().TotalSeconds));
        _agentStatusTimer.Start();
    }

    private TimeSpan TimeUntilTrayCheck() => _lastTrayCheckAt is { } checkedAt
        ? TimeSpan.FromMinutes(_preferences.TrayCheckMinutes) - _trayClock.GetElapsedTime(checkedAt)
        : TimeSpan.Zero;

    internal async Task PollStatusAsync()
    {
        if (!_config.IsManagedDeployment && !_windowClosed) StartStatusPolling();
        if (_windowClosed || _running || _agentStatusRefreshing) return;
        if (_config.IsManagedDeployment && !await RefreshAgentStatusAsync())
        {
            if (_windowClosed || _trayCatalogLoader is null) return;
            _lastTrayCheckAt = null;
            SetTrayStatus(new([], ConnectionFailed: true));
            return;
        }
        if (_trayCatalogLoader is not null && StartupCompletion.IsCompleted && TimeUntilTrayCheck() <= TimeSpan.Zero)
            await CheckTrayUpdatesAsync();
    }

    internal async Task CheckTrayUpdatesAsync()
    {
        if (!TrayCheckAvailable || _agentStatusRefreshing || _trayCatalogLoader is not { } loadCatalog) return;
        var capturedConfig = _config;
        var context = DisplaySelectionContext();
        var usesDistribution = UsesDistributionServer;
        var path = ConfigPath;
        var hidden = _trayHiddenProjects;
        var projects = new List<TrayProjectStatus>();
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_trayLifetime.Token);
        check.CancelAfter(TimeSpan.FromMinutes(2));
        var token = check.Token;
        _lastTrayCheckAt = _trayClock.GetTimestamp();
        if (!_config.IsManagedDeployment) StartStatusPolling();
        _running = true;
        SetBusy(true);
        try
        {
            var catalogConfig = await RunProjectConfig(_selectedProject, context, readOnly: true, token: token);
            var catalog = await loadCatalog(catalogConfig, context.Platform, token);
            if (usesDistribution || !string.IsNullOrWhiteSpace(catalogConfig.CatalogUrl))
            {
                foreach (var project in catalog.Projects.Where(project => !hidden.Contains(project.ProjectId)))
                {
                    token.ThrowIfCancellationRequested();
                    var releases = catalog.Releases.Where(release =>
                        string.Equals(release.ProjectId, project.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(release.Environment, context.Environment, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(release.Channel, context.Channel, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(release.Platform, context.Platform, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (releases.Count == 0) continue;
                    var recommendation = CatalogRecommendation.Find(catalog.SelectionPolicy, releases, release => release.IsLatest);
                    if (recommendation is null)
                    {
                        projects.Add(new(project.DisplayName, "실행 버전 지정 대기"));
                        continue;
                    }

                    var selection = new ReleaseSelection(recommendation.ProjectId, recommendation.Environment,
                        recommendation.Channel, recommendation.Platform, recommendation.Version);
                    var target = _config.Projects.FirstOrDefault(item =>
                        string.Equals(item.ProjectId, project.ProjectId, StringComparison.OrdinalIgnoreCase))
                        ?? new ProjectUiConfig { ProjectId = project.ProjectId, InstallPath = $"apps/{project.ProjectId}" };
                    // Standalone catalogs bind by exact version rather than a versioned installation ID.
                    var read = new LauncherUiOperationContext(context.Managed, selection.ProjectId, selection.Environment,
                        selection.Channel, selection.Platform, "exact", selection.Version, usesDistribution ? selection : null);
                    projects.Add(await ReadTrayProjectAsync(target, read, project.DisplayName, recommendation, token));
                }
            }
            else if (HasProject)
            {
                projects.Add(await ReadTrayProjectAsync(_selectedProject, context, _selectedProject.DisplayName, null, token));
            }

            if (MatchesTrayContext())
            {
                _trayTrack = (context.Environment, context.Channel);
                SetTrayStatus(new(projects));
            }
        }
        catch (Exception) when (_windowClosed) { }
        catch (Exception error)
        {
            if (MatchesTrayContext()) SetTrayStatus(new([], ConnectionFailed: error is AgentConnectionException, CheckFailed: true));
        }
        finally
        {
            _running = false;
            if (!_windowClosed) SetBusy(false);
        }

        bool MatchesTrayContext() => !_windowClosed && ReferenceEquals(_config, capturedConfig) &&
            ConfigPath == path && DisplaySelectionContext() == context;
    }

    private async Task<TrayProjectStatus> ReadTrayProjectAsync(ProjectUiConfig project, LauncherUiOperationContext context,
        string name, CatalogReleaseOption? recommendation, CancellationToken token)
    {
        try
        {
            var config = await RunProjectConfig(project, context, readOnly: true, token: token);
            var result = await _uiBackend.CheckAsync(context, config, _ => { }, token);
            context.Validate(result);
            var status = result.Status;
            if (recommendation is not null &&
                !string.Equals(status.AvailableVersion, recommendation.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The checked version is not the requested recommendation.");
            var previous = status.PreviousInstallation?.Release;
            var previousMatchesTrack = previous is not null &&
                string.Equals(previous.ProjectId, context.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Environment, context.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Channel, context.Channel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.Platform, context.Platform, StringComparison.OrdinalIgnoreCase);
            var installed = status.IsInstalled ? status.InstalledVersion : previousMatchesTrack ? previous!.Version : null;
            var hasUpdate = installed is not null && status.AvailableVersion is { } available &&
                !string.Equals(installed, available, StringComparison.OrdinalIgnoreCase);
            var label = RecommendationChange.Find("latest", recommendation, status) is not null
                ? "권장 버전 변경"
                : hasUpdate ? "업데이트 있음"
                : !status.IsInstalled ? "설치 필요"
                : status.UpdateRequired ? "확인 필요" : "최신 상태";
            return new(name, label, hasUpdate);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not AgentConnectionException)
        {
            return new(name, "확인 필요");
        }
    }

    private void SetTrayStatus(LauncherTrayStatus status)
    {
        if (status.ConnectionFailed == TrayStatus.ConnectionFailed && status.CheckFailed == TrayStatus.CheckFailed &&
            status.Projects.SequenceEqual(TrayStatus.Projects))
            return;
        TrayStatus = status;
        TrayStatusChanged?.Invoke();
    }

    private (CheckBox Keep, ComboBox Interval) TrayPreferenceControls()
    {
        const string keepLabel = "창을 닫으면 알림 영역에 남기기";
        var keep = Identify(new CheckBox
        {
            Content = keepLabel, IsChecked = _preferences.KeepInTray, Foreground = Fg(),
            FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale
        }, "keep-in-tray", keepLabel);
        var interval = Identify(new ComboBox
        {
            ItemsSource = LauncherUiPreferences.TrayCheckIntervals.Select(minutes => $"{minutes}분").ToArray(),
            SelectedIndex = Array.IndexOf(LauncherUiPreferences.TrayCheckIntervals, _preferences.TrayCheckMinutes),
            MinHeight = 40, FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale
        }, "tray-check-interval", "확인 주기");
        return (keep, interval);
    }
}
