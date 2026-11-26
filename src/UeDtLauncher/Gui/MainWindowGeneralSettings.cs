using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private static string LauncherDisplayVersion => LauncherBuildInfo.Version.Split('+', 2)[0];
    private string? _serviceVersion;
    private DateTimeOffset? _lastServiceCheck;
    private event Action? _settingsConnectionChanged;

    private Window ShowGeneralSettings()
    {
        var openedWith = _preferences;
        var connection = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm };
        void ShowConnection()
        {
            connection.Children.Clear();
            connection.Children.Add(KeyValue("연결", _agentState));
            if (_lastServiceCheck is { } checkedAt)
                connection.Children.Add(KeyValue("마지막 연결 확인", checkedAt.ToString("HH:mm")));
            if (_serviceVersion is { } version)
                connection.Children.Add(KeyValue("업데이트 서비스 버전", version));
        }
        ShowConnection();

        var installDirectory = SelectedInstallDirectory();
        var general = SettingsPanel(DialogCard(connection));
        if (PortableServerAddress() is { } address)
            general.Children.Add(DialogCard(KeyValue("서버 주소", address)));
        if (installDirectory is not null)
        {
            var openInstall = EnterpriseButton("설치 폴더", "open-folder", (_, _) => OpenInstallFolder(), tracked: false);
            general.Children.Add(DialogCard(SettingsPanel(KeyValue("설치 위치", installDirectory), openInstall)));
        }

        Window? dialog = null;
        var feedback = Txt("", LauncherVisualTokens.FontBody, false);
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        var supportId = _presentation.SupportId;
        var supportTime = supportId is null ? (DateTimeOffset?)null : _errorTime;
        var info = SettingsPanel(DialogCard(SettingsPanel(
            KeyValue("런처 버전", LauncherDisplayVersion),
            KeyValue("PC 이름", Environment.MachineName),
            KeyValue("지원 ID", LauncherUiError.IsSupportTag(supportId) ? supportId! : "없음"))));
        info.Children.Add(EnterpriseButton("지원 정보 복사", "settings-copy-support-info", async (_, _) =>
        {
            try
            {
                if (dialog!.Clipboard is not { } clipboard) throw new InvalidOperationException();
                await clipboard.SetTextAsync(SupportInfo(supportId, supportTime ?? DateTimeOffset.Now));
                feedback.Text = "지원 정보를 복사했습니다.";
            }
            catch { feedback.Text = "지원 정보를 복사할 수 없습니다."; }
        }, tracked: false));
        if (LauncherUiError.IsSupportTag(supportId))
            info.Children.Add(EnterpriseButton("지원 ID 복사", "copy-support-id", async (_, _) =>
            {
                try
                {
                    if (dialog!.Clipboard is not { } clipboard) throw new InvalidOperationException();
                    await clipboard.SetTextAsync(supportId!);
                    feedback.Text = "지원 ID를 복사했습니다.";
                }
                catch { feedback.Text = "지원 ID를 복사할 수 없습니다."; }
            }, tracked: false));

        var scale = Identify(new ComboBox
        {
            ItemsSource = new[] { "100%", "125%", "150%", "200%" },
            SelectedIndex = Array.IndexOf(LauncherUiPreferences.SupportedScales, _preferences.TextScale),
            MinHeight = 40,
            FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale
        }, "text-scale", "글자 크기");
        var contrast = Identify(new CheckBox
        {
            Content = "앱 고대비 사용",
            IsChecked = _preferences.HighContrast,
            Foreground = Fg(),
            FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale
        }, "high-contrast", "앱 고대비 사용");
        var display = SettingsPanel(DialogCard(SettingsPanel(
            Txt("글자 크기", LauncherVisualTokens.FontBody, true), scale, contrast,
            Muted("운영체제에서 고대비를 요청하면 앱 설정과 관계없이 고대비로 표시합니다.", LauncherVisualTokens.FontCaption))),
            EnterpriseButton("처음 안내 다시 보기", "settings-first-run-guide", (_, _) => ShowFirstRunGuide(dialog!), tracked: false));

        var readiness = EnterpriseButton("연결·준비 상태 점검", "settings-readiness", (_, _) =>
        {
            dialog!.Close();
            ShowReadinessDialog();
        }, tracked: false);
        var logFolder = EnterpriseButton("로그 폴더 열기", "settings-log-folder", async (_, _) =>
        {
            try
            {
                var path = UserLogDirectory();
                var opened = await Task.Run(() =>
                {
                    if (!Directory.Exists(path)) return false;
                    using var folder = Process.Start(new ProcessStartInfo
                    {
                        FileName = PortableMaintenance.ExistingFolder(path), UseShellExecute = true
                    });
                    return true;
                });
                feedback.Text = opened ? "로그 폴더를 열었습니다." : "아직 저장된 로그 폴더가 없습니다.";
            }
            catch { feedback.Text = "로그 폴더를 열 수 없습니다."; }
        }, tracked: false);
        var reload = EnterpriseButton("설정 다시 읽기", "settings-reload", async (sender, _) =>
        {
            var button = (Button)sender!;
            button.IsEnabled = false;
            try
            {
                var path = ConfigPath;
                var result = await Task.Run(() => ReadDisplayConfig(path));
                if (!dialog!.IsVisible || _running) return;
                dialog.Close();
                await ReloadDisplayConfigurationAsync(result);
            }
            finally { button.IsEnabled = true; }
        }, tracked: false);
        reload.IsEnabled = !_running;
        var troubleshooting = SettingsPanel(readiness, logFolder, reload);
        foreach (var (label, id, backups) in new[]
        {
            ("임시 파일 정리", "cache-clear", false),
            ("백업 정리", "backup-cleanup", true)
        })
        {
            var clean = EnterpriseButton(label, id, async (_, _) =>
            {
                if (!CanCleanFromSettings) return;
                dialog!.Close();
                await RunMaintenanceAsync(backups);
            }, tracked: false);
            clean.IsEnabled = CanCleanFromSettings;
            troubleshooting.Children.Add(clean);
        }
        if (_config.IsManagedDeployment)
            troubleshooting.Children.Add(Muted("임시 파일과 백업 정리는 관리자에게 문의해 주세요.", LauncherVisualTokens.FontCaption));

        var tabs = Identify(new TabControl
        {
            ItemsSource = new[]
            {
                SettingsTab("일반", "general", general),
                SettingsTab("정보", "info", info),
                SettingsTab("화면", "display", display),
                SettingsTab("문제 해결", "troubleshooting", troubleshooting)
            },
            // Keep the existing accessibility settings reachable with Tab on entry.
            SelectedIndex = 2
        }, "settings-tabs", "설정 탭");
        var close = Identify(SecondaryButton("닫기", (_, _) => dialog!.Close(), 40), "dialog-cancel", "닫기");
        var export = EnterpriseButton("지원 로그 ZIP 저장", "settings-export", async (sender, _) =>
        {
            var button = (Button)sender!;
            button.IsEnabled = false;
            try
            {
                feedback.Text = await ExportLogsZipAsync()
                    ? "지원 로그 ZIP을 저장했습니다."
                    : "지원 로그 ZIP을 저장할 수 없습니다.";
            }
            finally { button.IsEnabled = true; }
        }, tracked: false);
        var apply = Identify(PrimaryButton("적용", async (sender, _) =>
        {
            var button = (Button)sender!;
            button.IsEnabled = false;
            try
            {
                await SaveDisplayPreferencesAsync(openedWith, Math.Max(0, scale.SelectedIndex), contrast.IsChecked == true);
                dialog!.Close();
                Build();
                RefreshOpenDialogs();
                RecordDisplayDiagnostic();
            }
            catch { feedback.Text = "화면 설정을 저장할 수 없습니다."; }
            finally { button.IsEnabled = true; }
        }, 40), "dialog-confirm", "화면 설정 적용");
        tabs.SelectionChanged += (_, args) =>
        {
            if (args.Source == tabs)
            {
                apply.IsVisible = tabs.SelectedIndex == 2;
                feedback.Text = "";
                RefreshOpenDialogs();
            }
        };

        dialog = AccessibleDialog("설정", SettingsPanel(tabs, Identify(feedback, "settings-feedback", "설정 작업 결과")), close, export, apply);
        _settingsConnectionChanged += ShowConnection;
        dialog.Closed += (_, _) => _settingsConnectionChanged -= ShowConnection;
        _ = dialog.ShowDialog(this);
        return dialog;
    }

    private bool CanCleanFromSettings => !_config.IsManagedDeployment && _configurationError is null &&
        !_running && HasProject && !_readOnlyRecoveryFollowUp &&
        _viewModel.GeneralState != GeneralLauncherState.RuntimeBlocked;

    private string? PortableServerAddress()
    {
        if (_config.IsManagedDeployment || _configurationError is not null) return null;
        var url = _config.DistributionServerUrl ?? _config.CatalogUrl ?? _config.ManifestUrl;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : null;
    }

    private TabItem SettingsTab(string label, string id, Control content) =>
        Identify(ScaleHeader(new TabItem { Header = label, Content = content }), "settings-tab-" + id, label);

    private static StackPanel SettingsPanel(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = LauncherVisualTokens.SpaceMd };
        panel.Children.AddRange(controls);
        return panel;
    }

    private async Task ReloadDisplayConfigurationAsync((LauncherConfig Config, Exception? Error) display)
    {
        _agentStatusTimer.Stop();
        _selectedRuntimeConfig = null;
        _viewModel.ProjectStatus = null;
        _catalog = new();
        _resumeOperation = null;
        _damageDiagnostic = null;
        _lastDoctorReport = null;
        _presentation.InstallationNotice = null;
        _presentation.ErrorCode = _presentation.SupportId = null;
        _presentation.Retry = null;
        ApplyDisplayConfig(display);
        var appliedConfig = _config;
        if (_configurationError is null) _viewModel.GeneralState = GeneralLauncherState.Checking;
        Build();
        await RebindUserLoggerAsync();
        if (_configurationError is null && ReferenceEquals(_config, appliedConfig))
        {
            if (await RefreshCatalog(false, suppressDialog: true) && HasProject)
                await RefreshAuthorizedSelectionAsync(suppressDialog: true);
            if (_allowAutomaticChecks && _config.IsManagedDeployment && IsVisible)
            {
                await RefreshAgentStatusAsync();
                _agentStatusTimer.Start();
            }
        }
    }
}
