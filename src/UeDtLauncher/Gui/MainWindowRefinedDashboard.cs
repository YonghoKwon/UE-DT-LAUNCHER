using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private readonly LauncherStartupOptions _startupOptions;
    private readonly LauncherDashboardViewModel _viewModel = new();
    private LauncherConfig _config { get => _viewModel.Config; set => _viewModel.Config = value; }
    private ProjectUiConfig _selectedProject { get => _viewModel.SelectedProject; set => _viewModel.SelectedProject = value; }
    private CatalogSnapshot _catalog { get => _viewModel.Catalog; set => _viewModel.Catalog = value; }
    private string _search { get => _viewModel.Search; set => _viewModel.Search = value; }
    private string _catalogState { get => _viewModel.CatalogState; set => _viewModel.CatalogState = value; }
    private string _installState { get => _viewModel.InstallState; set => _viewModel.InstallState = value; }
    private string _installDetail { get => _viewModel.InstallDetail; set => _viewModel.InstallDetail = value; }
    private string _releaseNotes { get => _viewModel.ReleaseNotes; set => _viewModel.ReleaseNotes = value; }
    private bool _running { get => _viewModel.Running; set => _viewModel.Running = value; }
    private string _agentState { get => _viewModel.AgentState; set => _viewModel.AgentState = value; }
    private bool _agentStatusRefreshing;
    private bool _startupInitialized;
    private Exception? _configurationError;
    private readonly ILauncherUiBackend _uiBackend;
    private readonly Func<string, Task<LauncherConfig>> _runtimeConfigLoader;
    private readonly Func<LauncherConfig, RollbackPreview, Task<LauncherRestoreResult>> _restorePreview;
    private readonly Func<LauncherConfig, Task<RollbackPreview?>> _managedRollbackPreview;
    private readonly Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>> _catalogLoader;
    private readonly Func<string, bool, DoctorTarget?, CancellationToken, Task<DoctorReport>> _doctor;
    private bool _windowMetricsInitialized;
    private readonly bool _allowAutomaticChecks;
    private EventHandler<Avalonia.Platform.PlatformColorValues>? _colorValuesChanged;
    private int _layoutBucket = -1;
    private int _nextTabIndex;
    private readonly DispatcherTimer _agentStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, Bitmap> _projectVisualCache = new(StringComparer.OrdinalIgnoreCase);

    private TextBox? _configPathBox;
    private TextBox? _logBox;
    private TextBlock? _statusText;
    private TextBlock? _percentText;
    private TextBlock? _installStateText;
    private TextBlock? _installDetailText;
    private ProgressBar? _progress;
    private StackPanel? _projectList;
    private readonly List<Button> _actionButtons = new();
    private FileLogger? _fileLogger;

    // Download speed tracking for the status line.
    private long _speedLastBytes;
    private long _speedLastTickMs;
    private double _speedBytesPerSecond;

    private string BaseDir => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    private string ConfigPath => ResolvePath(_configPathBox?.Text ?? _configDraft ?? _startupOptions.ConfigPath);
    private bool IsDeveloper => _viewModel.IsDeveloper;
    private string CurrentPlatform => OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    private ProjectStatePaths SelectedStatePaths =>
        _selectedRuntimeConfig is { } runtime
            ? new ProjectStatePaths(Path.GetDirectoryName(runtime.InstallStatePath)!, runtime.StagingDir, runtime.BackupDir,
                runtime.InstalledManifestPath, runtime.InstallStatePath, runtime.AppPidPath, LauncherPaths.UpdateLockPath(runtime))
            : LauncherPaths.For(_config, ConfigPath, _selectedProject.ProjectId, CurrentPlatform);
    private LauncherConfig? _selectedRuntimeConfig;
    private bool UsesDistributionServer => _config.IsManagedDeployment || !string.IsNullOrWhiteSpace(_config.DistributionServerUrl);

    public MainWindow() : this(LauncherStartupOptions.Discover(Array.Empty<string>()))
    {
    }

    public MainWindow(LauncherStartupOptions startupOptions) : this(startupOptions, null, null, true) { }

    internal MainWindow(LauncherStartupOptions startupOptions, LauncherDashboardViewModel? model, LauncherUiPreferences? preferences, bool startServices, ILauncherUiBackend? backend = null,
        Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>>? catalogLoader = null,
        Func<string, bool, DoctorTarget?, CancellationToken, Task<DoctorReport>>? doctor = null,
        Func<string, Task<LauncherConfig>>? runtimeConfigLoader = null,
        Func<LauncherConfig, RollbackPreview, Task<LauncherRestoreResult>>? restorePreview = null,
        Func<LauncherConfig, Task<RollbackPreview?>>? managedRollbackPreview = null)
    {
        _uiBackend = backend ?? new LauncherUiBackend();
        _runtimeConfigLoader = runtimeConfigLoader ?? (path => LauncherPaths.LoadResolvedAsync(path));
        _managedRollbackPreview = managedRollbackPreview ?? PreviewManagedRollbackAsync;
        _restorePreview = restorePreview ?? ((config, preview) => config.IsManagedDeployment
            ? RestoreManagedPreviewAsync(config, preview)
            : Task.Run(async () => { await RollbackPreviewService.RestoreExpectedAsync(config, preview.BackupId, preview.MetadataFingerprint); return new LauncherRestoreResult(LauncherRestoreDisposition.Applied); }));
        _catalogLoader = catalogLoader ?? CatalogSnapshotService.LoadAsync;
        _doctor = doctor ?? ((path, online, target, token) => LauncherDoctor.RunAsync(path, online, token, target: target));
        _startupOptions = startupOptions;
        _allowAutomaticChecks = startServices;
        if (model is not null) _viewModel = model;
        _preferences = preferences ?? (startServices ? LauncherUiPreferences.Load() : new());
        InitializeComponent();
        if (model is null) LoadConfig(); else SelectProject();
        if (startServices) { try { _fileLogger = new FileLogger(_config.IsManagedDeployment ? ManagedClientContext.UserLogRoot(ConfigPath) : LauncherPaths.ResolveConfigRelative(ConfigPath, _config.LogDir)); } catch { _fileLogger = null; } }
        Build();
        if (startServices && PlatformSettings is not null)
        {
            _colorValuesChanged = (_, _) => Dispatcher.UIThread.Post(() => { Build(); RefreshOpenDialogs(); RecordDisplayDiagnostic(); });
            PlatformSettings.ColorValuesChanged += _colorValuesChanged;
        }
        _agentStatusTimer.Tick += async (_, _) => await RefreshAgentStatusAsync();
        if (startServices && _config.IsManagedDeployment && _configurationError is null) _agentStatusTimer.Start();
        Closed += (_, _) =>
        {
            _agentStatusTimer.Stop();
            if (_colorValuesChanged is not null && PlatformSettings is not null) PlatformSettings.ColorValuesChanged -= _colorValuesChanged;
            foreach (var bitmap in _projectVisualCache.Values) bitmap.Dispose();
            _projectVisualCache.Clear();
            _brandLogo?.Dispose();
        };
        if (startServices) Opened += async (_, _) => { RecordDisplayDiagnostic(); await InitializeStartupAsync(); };
        SizeChanged += (_, args) =>
        {
            UpdateActionDetailsLimit(); RecordDisplayDiagnostic();
            var nextBucket = GeneralLayoutBucket(args.NewSize.Width);
            if (_windowMetricsInitialized && nextBucket != _layoutBucket)
            {
                _layoutBucket = nextBucket;
                Build();
            }
        };
    }

    private async Task InitializeStartupAsync()
    {
        if (_startupInitialized) return;
        _startupInitialized = true;
        if (!File.Exists(ConfigPath) || _configurationError is not null)
        {
            _viewModel.GeneralState = GeneralLauncherState.ConfigurationRequired;
            return;
        }

        _viewModel.GeneralState = GeneralLauncherState.Checking;
        var serviceAvailable = await RefreshAgentStatusAsync();
        if (_config.IsManagedDeployment && !serviceAvailable)
        {
            _installState = "확인 필요";
            _installDetail = "업데이트 서비스에 연결한 뒤 다시 확인해 주세요.";
            _viewModel.GeneralState = GeneralLauncherState.RecoverableError;
            _presentation.Title = "업데이트 서비스 연결이 필요합니다"; _presentation.Percent = null;
            _presentation.ErrorCode = "service-unavailable";
            _presentation.SupportId = Guid.NewGuid().ToString("N");
            Build();
            return;
        }
        if (await RefreshCatalog(false, suppressDialog: true) && HasProject) await RefreshInstallStatusAsync(suppressDialog: true);
    }

    private void LoadConfig()
    {
        _configurationError = null;
        _config = new LauncherConfig();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath), JsonFiles.Options)
                    ?? throw new ArgumentException("Launcher configuration is empty.");
                if (parsed.Projects is null || parsed.Security is null || parsed.Performance is null ||
                    parsed.Projects.Any(p => p is null || string.IsNullOrWhiteSpace(p.ProjectId) || p.VisibleToProfiles is null || p.VisibleToProfiles.Any(v => v is null)))
                    throw new ArgumentException("Launcher display configuration is incomplete.");
                foreach (var project in parsed.Projects) if (string.IsNullOrWhiteSpace(project.DisplayName)) project.DisplayName = project.ProjectId;
                _config = parsed;
            }
            catch (Exception ex)
            {
                _configurationError = ex; var error = LauncherUiError.From(new ArgumentException("Invalid launcher configuration.", ex));
                _installState = "설정 확인 필요"; _installDetail = error.Message;
                _viewModel.GeneralState = GeneralLauncherState.ConfigurationRequired;
                _presentation.Title = "런처 설정을 확인해 주세요."; _presentation.ErrorCode = "configuration-invalid"; _presentation.SupportId = error.SupportId;
            }
        }
        else
        {
            _configurationError = new FileNotFoundException("Launcher display configuration is missing.");
            _presentation.Title = "런처 설정을 확인해 주세요."; _presentation.ErrorCode = "configuration-invalid"; _presentation.SupportId = Guid.NewGuid().ToString("N");
            _installState = "설정 확인 필요";
            _installDetail = IsDeveloper
                ? $"설정 파일이 없습니다: {ConfigPath}"
                : "런처 설정이 필요합니다. 관리자에게 문의해 주세요.";
            _viewModel.GeneralState = GeneralLauncherState.ConfigurationRequired;
        }

        _config.TargetPlatform = CurrentPlatform;
        if (!IsDeveloper)
        {
            _config.Environment = "prod";
            _config.Channel = "stable";
            _config.VersionPolicy = "latest";
            _config.RequestedVersion = null;
        }
        _agentState = _config.IsManagedDeployment ? LauncherDashboardViewModel.ConnectionLabel(IsDeveloper, ServiceConnectionState.Checking) : "로컬 모드";

        if (_config.Projects.Count == 0)
        {
            _config.Projects.Add(new ProjectUiConfig
            {
                ProjectId = _config.ProjectId ?? "ue-dt-project",
                DisplayName = _config.ProjectId ?? "UE-DT 프로젝트",
                Description = IsDeveloper ? "개발자용 배포 프로젝트입니다." : "운영 안정화 배포 프로젝트입니다.",
                Status = IsDeveloper ? "개발 중" : "안정 버전",
                InstallPath = _config.InstallDir,
                Technology = CurrentPlatform,
                IsPinned = true
            });
        }
        SelectProject();
    }

    private void SelectProject()
    {
        var projects = VisibleProjects().ToList();
        _selectedProject = projects.FirstOrDefault(p => string.Equals(p.ProjectId, _config.ProjectId, StringComparison.OrdinalIgnoreCase))
            ?? projects.FirstOrDefault()
            ?? new ProjectUiConfig { ProjectId = "", DisplayName = "사용 가능한 프로젝트가 없습니다" };
        _config.ProjectId = _selectedProject.ProjectId;
        UpdateReleaseNotes();
    }

    private IEnumerable<ProjectUiConfig> VisibleProjects()
    {
        return _viewModel.VisibleProjects();
    }

    private void Build() => BuildEnterprise();

    private double CurrentLayoutWidth() => ClientSize.Width > 0 ? ClientSize.Width : Width;
    private int VisibleProjectCount() => _viewModel.ProjectsForProfile().Take(2).Count();
    private LauncherLayoutPolicy CurrentLayout() => LauncherLayoutPolicy.For(CurrentLayoutWidth(), VisibleProjectCount(), IsDeveloper);
    private static int GeneralLayoutBucket(double width) => width < 800 ? 0 : width < 1100 ? 1 : 2;


}
