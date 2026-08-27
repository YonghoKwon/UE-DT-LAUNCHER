using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UeDtLauncher.Gui;

public enum GeneralLauncherState
{
    Initializing,
    ConfigurationRequired,
    Checking,
    NotInstalled,
    UpdateAvailable,
    Ready,
    Working,
    RecoverableError
}

public enum PrimaryActionKind
{
    Disabled,
    InstallAndLaunch,
    UpdateAndLaunch,
    Launch,
    RetryCheck
}

public enum ServiceConnectionState
{
    Checking,
    Connected,
    Disconnected,
    Error
}

public enum LauncherWorkflowStage
{
    None,
    Catalog,
    Download,
    Verify,
    Apply,
    Launch,
    Complete
}

public sealed record LauncherUiCapabilities(
    bool CanChangeReleaseTrack,
    bool CanRepair,
    bool CanClearCache,
    bool CanManageBackups,
    bool CanViewTechnicalErrors,
    bool CanEditConfigPath)
{
    public static LauncherUiCapabilities ForProfile(string? profile) =>
        string.Equals(profile, "developer", StringComparison.OrdinalIgnoreCase)
            ? new(true, true, true, true, true, true)
            : new(false, false, false, false, false, false);
}

public sealed class LauncherDashboardViewModel : INotifyPropertyChanged
{
    private LauncherConfig _config = new();
    private ProjectUiConfig _selectedProject = new();
    private CatalogSnapshot _catalog = new();
    private string _search = string.Empty;
    private string _catalogState = "카탈로그 미확인";
    private string _installState = "확인 필요";
    private string _installDetail = "상태 확인을 눌러 설치 상태를 확인하세요.";
    private string _releaseNotes = "릴리스 노트가 없습니다.";
    private string _agentState = "Agent 확인 중";
    private GeneralLauncherState _generalState = GeneralLauncherState.Initializing;
    private ManagedProjectStatus? _projectStatus;
    private LauncherWorkflowStage _workflowStage;
    private bool _running;

    public LauncherConfig Config { get => _config; set => Set(ref _config, value); }
    public ProjectUiConfig SelectedProject { get => _selectedProject; set => Set(ref _selectedProject, value); }
    public CatalogSnapshot Catalog { get => _catalog; set => Set(ref _catalog, value); }
    public string Search { get => _search; set => Set(ref _search, value); }
    public string CatalogState { get => _catalogState; set => Set(ref _catalogState, value); }
    public string InstallState { get => _installState; set => Set(ref _installState, value); }
    public string InstallDetail { get => _installDetail; set => Set(ref _installDetail, value); }
    public string ReleaseNotes { get => _releaseNotes; set => Set(ref _releaseNotes, value); }
    public string AgentState { get => _agentState; set => Set(ref _agentState, value); }
    public GeneralLauncherState GeneralState { get => _generalState; set => Set(ref _generalState, value); }
    public ManagedProjectStatus? ProjectStatus { get => _projectStatus; set => Set(ref _projectStatus, value); }
    public LauncherWorkflowStage WorkflowStage { get => _workflowStage; set => Set(ref _workflowStage, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
    public bool IsDeveloper => Capabilities.CanViewTechnicalErrors;
    public LauncherUiCapabilities Capabilities => LauncherUiCapabilities.ForProfile(Config.ClientProfile);
    public PrimaryActionKind PrimaryAction => GeneralState switch
    {
        GeneralLauncherState.NotInstalled => PrimaryActionKind.InstallAndLaunch,
        GeneralLauncherState.UpdateAvailable => PrimaryActionKind.UpdateAndLaunch,
        GeneralLauncherState.Ready => PrimaryActionKind.Launch,
        GeneralLauncherState.RecoverableError or GeneralLauncherState.ConfigurationRequired => PrimaryActionKind.RetryCheck,
        _ => PrimaryActionKind.Disabled
    };

    public string PrimaryActionText => PrimaryAction switch
    {
        PrimaryActionKind.InstallAndLaunch => "설치 후 실행",
        PrimaryActionKind.UpdateAndLaunch => "업데이트 후 실행",
        PrimaryActionKind.Launch => "실행",
        PrimaryActionKind.RetryCheck => "다시 확인",
        _ => "상태 확인 중..."
    };

    public void ApplyProjectStatus(ManagedProjectStatus status)
    {
        ProjectStatus = status;
        GeneralState = !status.IsInstalled
            ? GeneralLauncherState.NotInstalled
            : status.UpdateRequired
                ? GeneralLauncherState.UpdateAvailable
                : GeneralLauncherState.Ready;
    }

    public static string ConnectionLabel(
        bool developer,
        ServiceConnectionState state,
        string? version = null) => (developer, state) switch
    {
        (true, ServiceConnectionState.Checking) => "Agent 확인 중",
        (true, ServiceConnectionState.Connected) => $"연결됨 {version}".TrimEnd(),
        (true, ServiceConnectionState.Error) => "Agent 오류",
        (true, _) => "Agent 미연결",
        (false, ServiceConnectionState.Checking) => "업데이트 서비스 확인 중",
        (false, ServiceConnectionState.Connected) => "업데이트 서비스 정상",
        (false, ServiceConnectionState.Error) => "업데이트 서비스 점검 필요",
        _ => "업데이트 서비스 연결 필요"
    };

    public void ApplyProgressStage(string stage)
    {
        WorkflowStage = stage switch
        {
            "Catalog" => LauncherWorkflowStage.Catalog,
            "Download" or "DownloadProgress" => LauncherWorkflowStage.Download,
            "Manifest" or "Plan" => LauncherWorkflowStage.Verify,
            "Apply" or "Package" or "Rollback" => LauncherWorkflowStage.Apply,
            "Launch" => LauncherWorkflowStage.Launch,
            _ => WorkflowStage
        };
    }

    public IEnumerable<ProjectUiConfig> VisibleProjects()
    {
        return Config.Projects
            .Where(project => project.VisibleToProfiles.Count == 0 ||
                              project.VisibleToProfiles.Any(profile => profile.Equals(Config.ClientProfile, StringComparison.OrdinalIgnoreCase)))
            .Where(project => string.IsNullOrWhiteSpace(Search) ||
                              project.ProjectId.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                              project.DisplayName.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(project => project.IsPinned)
            .ThenBy(project => project.SortOrder)
            .ThenBy(project => project.DisplayName, StringComparer.CurrentCultureIgnoreCase);
    }

    public string FriendlyError(Exception exception)
    {
        var message = DiagnosticRedactor.Redact(exception.GetBaseException().Message);
        if (IsDeveloper) return message;
        if (message.Contains("requestedVersion is required", StringComparison.OrdinalIgnoreCase))
            return "exact 버전을 사용하려면 요청 버전을 입력해야 합니다.";
        if (message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) || message.Contains("401", StringComparison.OrdinalIgnoreCase))
            return "이 PC의 업데이트 서버 접근 권한을 확인해 주세요.";
        if (message.Contains("No release", StringComparison.OrdinalIgnoreCase))
            return "현재 받을 수 있는 배포 버전이 없습니다.";
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return "업데이트 서버에 연결할 수 없습니다. 네트워크를 확인해 주세요.";
        if (message.Contains("signature", StringComparison.OrdinalIgnoreCase) || message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return "업데이트 보안 검증에 실패했습니다. 관리자에게 문의해 주세요.";
        return "작업 중 문제가 발생했습니다. 잠시 후 다시 시도하거나 관리자에게 문의하세요.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
