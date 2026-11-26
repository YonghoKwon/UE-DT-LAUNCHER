using Avalonia.Controls;
using Avalonia.Threading;

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
    private TimeSpan _trayQueryTimeout = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _trayCheck;
    private Task? _managedTrayQuery;
    private bool _trayCheckRequested;
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
    internal bool TrayCheckAvailable => !_windowClosed && !_exitingFromTray && _configurationError is null;
    internal Task TrayNoticeSaved => _trayNoticeSave;

    internal void EnableTray(Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>> loadCatalog,
        TimeProvider? clock = null, TimeSpan? queryTimeout = null)
    {
        _trayCatalogLoader = loadCatalog;
        _trayClock = clock ?? TimeProvider.System;
        if (queryTimeout is { } timeout) _trayQueryTimeout = timeout;
        Closing += HideToTrayOnClose;
        StartStatusPolling();
    }

    private void ResetTrayStatus()
    {
        _trayCheck?.Cancel();
        _trayCheckRequested = false;
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
        _trayCheck?.Cancel();
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
        if (!_preferences.HasSeenTrayNotice && _trayNoticeSave.IsCompleted)
            _trayNoticeSave = ClaimTrayNoticeAsync();
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
    }

    private async Task ClaimTrayNoticeAsync()
    {
        try
        {
            var (value, claimed) = await LauncherUiPreferences.UpdateAsync(_preferencesPath, latest =>
                latest.KeepInTray ? latest with { HasSeenTrayNotice = true } : latest);
            _preferences = _preferences with { HasSeenTrayNotice = value.HasSeenTrayNotice };
            if (claimed && !_windowClosed && !_exitingFromTray) ShowTrayNotice();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("알림 영역 안내를 본 기록을 저장하지 못했습니다.", force: true);
        }
    }

    internal bool OpenFromTray()
    {
        if (_windowClosed || _exitingFromTray) return false;
        if (_config.IsManagedDeployment) _trayCheck?.Cancel();
        _trayNotice?.Close();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return IsVisible && WindowState != WindowState.Minimized;
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
        if (_windowClosed || _running || _agentStatusRefreshing || _trayCheck is not null) return;
        if (_config.IsManagedDeployment && !await RefreshAgentStatusAsync())
        {
            if (_windowClosed || _trayCatalogLoader is null) return;
            _lastTrayCheckAt = null;
            SetTrayStatus(TrayStatus with { ConnectionFailed = true });
            await RunTrayCheckIfReadyAsync(automatic: false);
            return;
        }
        if (TrayStatus.ConnectionFailed)
            SetTrayStatus(TrayStatus with { ConnectionFailed = false });
        await RunTrayCheckIfReadyAsync(automatic: true);
    }

    internal Task CheckTrayUpdatesAsync()
    {
        if (!TrayCheckAvailable) return Task.CompletedTask;
        _trayCheckRequested = true;
        return RunTrayCheckIfReadyAsync(automatic: false);
    }

    private void DispatchPendingTrayCheck()
    {
        if (_trayCheckRequested)
            Dispatcher.UIThread.Post(async () => await RunTrayCheckIfReadyAsync(automatic: false), DispatcherPriority.Background);
    }

    private async Task RunTrayCheckIfReadyAsync(bool automatic)
    {
        if (!TrayCheckAvailable || _running || _agentStatusRefreshing || _trayCheck is not null ||
            _trayCatalogLoader is not { } loadCatalog) return;
        var manual = _trayCheckRequested;
        // The legacy Agent serializes commands. Reserve automatic managed reads for the hidden window.
        if (!manual && (!automatic || !StartupCompletion.IsCompleted || TimeUntilTrayCheck() > TimeSpan.Zero ||
            (_config.IsManagedDeployment && IsVisible))) return;
        _trayCheckRequested = false;
        var capturedConfig = _config;
        var context = DisplaySelectionContext();
        _trayTrack = (context.Environment, context.Channel);
        var usesDistribution = UsesDistributionServer;
        var path = ConfigPath;
        var hidden = _trayHiddenProjects;
        var projects = new List<TrayProjectStatus>();
        using var check = CancellationTokenSource.CreateLinkedTokenSource(_trayLifetime.Token);
        _trayCheck = check;
        var token = check.Token;
        _lastTrayCheckAt = _trayClock.GetTimestamp();
        if (!_config.IsManagedDeployment) StartStatusPolling();
        try
        {
            var (catalogConfig, catalog) = await QueryTrayAsync(context.Managed, async queryToken =>
            {
                var config = await RunProjectConfig(_selectedProject, context, readOnly: true, token: queryToken);
                return (config, await loadCatalog(config, context.Platform, queryToken));
            }, token);
            token.ThrowIfCancellationRequested();
            Publish();
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
                        Publish();
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
                    Publish();
                }
            }
            else if (HasProject)
            {
                projects.Add(await ReadTrayProjectAsync(_selectedProject, context, _selectedProject.DisplayName, null, token));
                Publish();
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            if (MatchesTrayContext())
            {
                _lastTrayCheckAt = null;
                _trayCheckRequested |= manual;
            }
        }
        catch (Exception error)
        {
            if (MatchesTrayContext())
                SetTrayStatus(new(projects.ToArray(), ConnectionFailed: error is AgentConnectionException, CheckFailed: true));
        }
        finally
        {
            _trayCheck = null;
            DispatchPendingTrayCheck();
        }

        bool MatchesTrayContext() => !_windowClosed && ReferenceEquals(_config, capturedConfig) &&
            ConfigPath == path && DisplaySelectionContext() == context;

        void Publish()
        {
            if (!token.IsCancellationRequested && MatchesTrayContext())
                SetTrayStatus(new(projects.ToArray()));
        }
    }

    private async Task<T> QueryTrayAsync<T>(bool managed, Func<CancellationToken, Task<T>> query, CancellationToken sessionToken)
    {
        sessionToken.ThrowIfCancellationRequested();
        // A disconnected client cannot cancel a v1 server command. Let the current managed read drain
        // on foreground handoff, and prevent the remainder of the scan from entering the Agent gate.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(managed ? _trayLifetime.Token : sessionToken);
        limit.CancelAfter(_trayQueryTimeout);
        var request = ReadAsync();
        if (managed) _managedTrayQuery = request;
        try
        {
            var result = await request;
            limit.Token.ThrowIfCancellationRequested();
            sessionToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (managed) _managedTrayQuery = null;
        }

        async Task<T> ReadAsync()
        {
            while (true)
            {
                sessionToken.ThrowIfCancellationRequested();
                limit.Token.ThrowIfCancellationRequested();
                try { return await query(limit.Token); }
                catch (AgentOperationException error) when (managed && error.ErrorCode == "busy")
                {
                    // An earlier request or another client can still be draining the shared Agent gate.
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, sessionToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), retry.Token);
                }
            }
        }
    }

    private async Task WaitForManagedTrayQueryAsync(CancellationToken token)
    {
        if (_managedTrayQuery is not { } request) return;
        try { await request.WaitAsync(token); }
        catch (Exception) when (!token.IsCancellationRequested) { /* The tray owns this query's error. */ }
    }

    private async Task<TrayProjectStatus> ReadTrayProjectAsync(ProjectUiConfig project, LauncherUiOperationContext context,
        string name, CatalogReleaseOption? recommendation, CancellationToken token)
    {
        try
        {
            var result = await QueryTrayAsync(context.Managed, async queryToken =>
            {
                var config = await RunProjectConfig(project, context, readOnly: true, token: queryToken);
                return await _uiBackend.CheckAsync(context, config, _ => { }, queryToken);
            }, token);
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
        catch (Exception) when (!token.IsCancellationRequested)
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
