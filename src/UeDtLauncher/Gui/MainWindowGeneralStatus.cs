using Avalonia.Controls;
using Avalonia.Layout;
using System.IO.Compression;
using System.Text.Json;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private DateTimeOffset _errorTime;
    private string _errorStage = "확인";
    private bool _errorDetailsExpanded;
    private (ReleaseSelection Recommended, ReleaseSelection Previous)? _explainedRecommendation;
    private (LauncherUiOperationContext Context, GeneralSupportDiagnostic Diagnostic)? _damageDiagnostic;

    private sealed record GeneralSupportDiagnostic(string Code, string Stage, string? SupportId, DateTimeOffset OccurredAt, string ComputerName);

    private bool SelectedInstallationDamaged => IsInstallationDamaged(_viewModel.ProjectStatus);

    private static bool IsInstallationDamaged(ManagedProjectStatus? projectStatus) => projectStatus is
        { IsInstalled: true, UpdateRequired: true } status &&
        string.Equals(status.InstalledVersion, status.AvailableVersion, StringComparison.OrdinalIgnoreCase);

    private (ReleaseSelection Recommended, ReleaseSelection Previous)? LowerRecommendation()
        => LowerRecommendation(_config.VersionPolicy, SelectedCatalogRelease(), _viewModel.ProjectStatus);

    private static (ReleaseSelection Recommended, ReleaseSelection Previous)? LowerRecommendation(
        string policy, CatalogReleaseOption? selected, ManagedProjectStatus? status)
    {
        if (policy != "latest" || selected is not { IsLatest: true } release ||
            status is not { IsInstalled: false, PreviousInstallation: { } previous, AvailableVersion: { } available } ||
            !string.Equals(available, release.Version, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(available.TrimStart('v', 'V'), out var recommended) ||
            !Version.TryParse(previous.Release.Version.TrimStart('v', 'V'), out var installed) || recommended >= installed)
            return null;
        return (new(release.ProjectId, release.Environment, release.Channel, release.Platform, release.Version), previous.Release);
    }

    private string GeneralActionText() => _viewModel.GeneralState switch
    {
        GeneralLauncherState.Checking => "상태 확인 중",
        GeneralLauncherState.NotInstalled => "설치",
        GeneralLauncherState.UpdateAvailable when SelectedInstallationDamaged => "문제 해결",
        GeneralLauncherState.UpdateAvailable when LowerRecommendation() is { } recommendation && _explainedRecommendation != recommendation => "권장 버전 변경 안내",
        GeneralLauncherState.UpdateAvailable => "업데이트",
        GeneralLauncherState.Working => "작업 중",
        _ => _viewModel.PrimaryActionText
    };

    private string GeneralStateDetail() => _viewModel.GeneralState switch
    {
        GeneralLauncherState.Checking => "배포와 설치 상태를 확인하고 있습니다.",
        GeneralLauncherState.NotInstalled => "처음 설치합니다. 설치 중에는 진행률이 표시되며, 완료되면 프로그램을 실행합니다.",
        GeneralLauncherState.UpdateAvailable when SelectedInstallationDamaged => "일부 파일이 손상됐습니다. 문제 해결은 손상된 파일만 다시 받습니다.",
        GeneralLauncherState.UpdateAvailable when LowerRecommendation() is not null => "현재 권장 버전이 이전 설치 기록보다 낮습니다.",
        GeneralLauncherState.UpdateAvailable => "업데이트할 수 있습니다. 완료되면 프로그램을 실행합니다.",
        GeneralLauncherState.Working => "요청한 작업을 진행하고 있습니다.",
        _ => _installDetail
    };

    private void ShowInstallationNotice(LauncherUiOperationResult result)
    {
        if (!IsDeveloper && (result.Selection?.Version ?? result.Status.InstalledVersion) is { } version)
            _presentation.InstallationNotice = $"{_selectedProject.DisplayName} {version} 설치를 마쳤습니다.";
    }

    private void SetErrorDetails(LauncherUiError error)
    {
        _presentation.ErrorCode = error.Code;
        _presentation.SupportId = error.SupportId;
        _errorTime = DateTimeOffset.Now;
        _errorStage = _presentation.CurrentStage;
        _errorDetailsExpanded = false;
    }

    internal static string SupportInfo(string? supportId, DateTimeOffset time) =>
        $"런처 버전: {LauncherDisplayVersion}\nPC 이름: {Environment.MachineName}\n지원 ID: {(LauncherUiError.IsSupportTag(supportId) ? supportId : "없음")}\n시각: {time:yyyy-MM-dd HH:mm:ss zzz}";

    private GeneralSupportDiagnostic CurrentSupportDiagnostic()
    {
        if (_presentation.ErrorCode is { } code)
            return new(code, _errorStage, _presentation.SupportId, _errorTime, Environment.MachineName);
        if (_viewModel.GeneralState == GeneralLauncherState.RuntimeBlocked)
            return new("runtime-blocked", "확인", null, DateTimeOffset.Now, Environment.MachineName);

        var context = DisplaySelectionContext();
        if (_damageDiagnostic is { } existing && existing.Context == context) return existing.Diagnostic;
        var diagnostic = new GeneralSupportDiagnostic("damaged-installation", "확인", Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now, Environment.MachineName);
        _damageDiagnostic = (context, diagnostic);
        return diagnostic;
    }

    private Control GeneralErrorDetails()
    {
        var diagnostic = CurrentSupportDiagnostic();
        var capturedInfo = SupportInfo(diagnostic.SupportId, diagnostic.OccurredAt);
        var copy = EnterpriseButton("지원 정보 복사", "copy-support-info", async (_, _) =>
        {
            if (Clipboard is not null) await Clipboard.SetTextAsync(capturedInfo);
        });
        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(Identify(Muted($"오류 코드 {diagnostic.Code} · 단계 {diagnostic.Stage}", 14), "safe-error-details", "오류 코드와 발생 단계"));
        details.Children.Add(Identify(Muted(capturedInfo, 14), "safe-support-info", "지원 정보"));
        details.Children.Add(EnterpriseButton("로그 ZIP 저장", "save-error-logs", async (_, _) =>
        {
            try
            {
                var folder = Path.Combine(Path.GetDirectoryName(LauncherUiPreferences.DefaultPath)!, "support");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, $"launcher-logs-{Guid.NewGuid():N}.zip");
                    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                    using var output = archive.CreateEntry("support.json").Open();
                    JsonSerializer.Serialize(output, diagnostic, JsonFiles.Options);
                });
                SetStatus("지원 로그 ZIP을 사용자 지원 폴더에 저장했습니다.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { SetStatus("지원 로그 ZIP을 저장하지 못했습니다. 저장 공간과 접근 권한을 확인해 주세요."); }
        }, icon: LauncherIconKind.Log));
        var expander = Identify(ScaleHeader(new Expander { Header = "자세히", Content = details, IsExpanded = _errorDetailsExpanded }),
            "error-details", "오류 자세히 보기");
        var content = new StackPanel { Spacing = 8, Children = { copy, expander } };
        var command = _configurationError is null ? null : ConfigurationExample();
        if (command is not null)
        {
            command.IsVisible = expander.IsExpanded;
            content.Children.Add(command);
        }
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty) return;
            _errorDetailsExpanded = expander.IsExpanded;
            if (command is not null) command.IsVisible = expander.IsExpanded;
        };
        return Card(content);
    }

    private TextBox ConfigurationExample() => Identify(new TextBox
    {
        Text = "UeDtLauncher.exe sample-config --mode managed-client --project-id <프로젝트ID> --output launcher.config.json",
        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 * _preferences.TextScale, MinHeight = 72
    }, "configuration-example", "관리형 화면 설정 생성 명령 예시");
}
