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
    private string? SelectedInstallDirectory()
    {
        if (_configurationError is not null || !HasProject) return null;
        if (UsesDistributionServer && SelectedCatalogRelease() is null) return null;
        var selection = CurrentReleaseSelection();
        if (selection is not null && _selectedRuntimeConfig?.SelectedRelease != selection) return null;
        if (_config.IsManagedDeployment) return _selectedRuntimeConfig?.ManagedPresentation?.InstallDirectory;
        if (_selectedRuntimeConfig is { } runtime) return runtime.InstallDir;
        if (UsesDistributionServer || string.IsNullOrWhiteSpace(_selectedProject.InstallPath ?? _config.InstallDir)) return null;
        try { return LauncherPaths.ResolveConfigRelative(ConfigPath, _selectedProject.InstallPath ?? _config.InstallDir); }
        catch (ArgumentException) { return null; }
    }

    private async void OpenInstallFolder()
    {
        var history = BeginDeveloperHistory("설치 폴더 열기");
        try
        {
            var path = SelectedInstallDirectory() ?? throw new InvalidOperationException("선택한 배포의 상태를 먼저 확인해 주세요.");
            await Task.Run(() =>
            {
                using var folder = Process.Start(new ProcessStartInfo
                {
                    FileName = PortableMaintenance.ExistingFolder(path), UseShellExecute = true
                });
            });
            history?.Finish(LauncherHistoryResult.Completed);
        }
        catch (Exception ex)
        {
            history?.Finish(LauncherHistoryResult.Failed);
            AppendLog("폴더를 열 수 없습니다: " + FriendlyError(ex), true);
            SetStatus("설치 폴더를 확인할 수 없습니다. 상태를 먼저 확인해 주세요.");
        }
    }
    private static Task<PortableMaintenanceResult> RunPortableMaintenanceAsync(
        LauncherConfig config, bool backups, Action<string> log) =>
        Task.Run(() => backups ? PortableMaintenance.PruneBackupsWithResult(config, log) : PortableMaintenance.ClearStagingWithResult(config, log));

    private async Task RunMaintenanceAsync(bool backups)
    {
        if (_running) return;
        if (_readOnlyRecoveryFollowUp) { await RefreshInstallStatusAsync(); return; }
        _running = true; SetBusy(true);
        var history = BeginDeveloperHistory(backups ? "백업 정리" : "임시 파일 정리");
        try
        {
            var context = CaptureUiOperation();
            BeginOperation(backups ? LauncherUiOperation.PruneBackups : LauncherUiOperation.ClearStaging);
            if (context.Managed) throw new InvalidOperationException("관리형 정리는 업데이트 서비스의 별도 관리 기능이 필요합니다.");
            var config = await RunConfig(false, false); context.Pin(config);
            if (context.Selection is not null) VersionedReleasePaths.Bind(config, context.Selection);
            var messages = new List<string>();
            var result = await _runPortableMaintenance(config, backups, messages.Add);
            foreach (var message in messages) AppendLog(message, true);
            if (result.FailedCount > 0 || !result.ResumeRecordsPreserved)
            {
                history?.Finish(LauncherHistoryResult.Failed);
                AppendLog($"정리 결과: 삭제 {result.RemovedCount}개 / 실패 {result.FailedCount}개 / 남은 {result.RemainingCount}개", true);
                MarkError(new AgentOperationException("maintenance-partial", Guid.NewGuid().ToString("N"), "사용 중인 파일과 권한을 확인한 뒤 정리를 다시 시도해 주세요."), backups ? "일부 백업 정리 필요" : "일부 임시 파일 정리 필요", showDialog: false);
            }
            else
            {
                history?.Finish(LauncherHistoryResult.Completed);
                var title = backups ? $"백업 정리 완료 · 삭제 {result.RemovedCount}개 · 남은 {result.RemainingCount}개 (보관 기준 {config.MaxBackupCount}개)" : "임시 파일 정리 완료 · 이어받기 기록 보존";
                BeginReadOnlyFollowUp("정리 완료");
                var followUp = BeginDeveloperHistory("상태 확인", "재확인");
                try
                {
                    var checkedResult = await _uiBackend.CheckAsync(context, config, CreateUiProgress(followUp));
                    if (ApplyUiResult(context, checkedResult)) { _presentation.Complete(title); _presentation.Retry = null; Build(); }
                    FinishHistoryForObservation(followUp, checkedResult.Runtime, "재확인");
                }
                catch (Exception error)
                {
                    followUp?.Finish(LauncherHistoryResult.Failed, "재확인");
                    _readOnlyRecoveryFollowUp = true; _presentation.Retry = CurrentContext(LauncherUiOperation.Check); MarkError(error, "정리 완료 · 상태 재확인 필요", showDialog: false);
                }
            }
        }
        catch (Exception ex) { history?.Finish(LauncherHistoryResult.Failed); MarkError(ex, "정리할 수 없습니다", showDialog: false); }
        finally { FinishUiOperation(); }
    }
    private async void ClearCache() => await RunMaintenanceAsync(false);
    private async void CleanupBackups() => await RunMaintenanceAsync(true);
    private void ClearLog()
    {
        _presentation.Logs.Clear();
        _developerHistory.Clear();
        if (_logBox is not null) _logBox.Text = string.Empty;
    }
    private string UserLogDirectory() => _config.IsManagedDeployment
        ? ManagedClientContext.UserLogRoot(ConfigPath)
        : LauncherPaths.ResolveConfigRelative(ConfigPath, _config.LogDir);

    private async Task RebindUserLoggerAsync()
    {
        _fileLogger = null;
        if (!_allowAutomaticChecks || _configurationError is not null) return;
        var config = _config;
        try
        {
            var path = UserLogDirectory();
            var logger = await Task.Run(() => new FileLogger(path));
            if (ReferenceEquals(_config, config))
            {
                _fileLogger = logger;
                _lastDisplayDiagnostic = null;
                RecordDisplayDiagnostic();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private async void ExportLogsZip() => await ExportLogsZipAsync();

    private async Task<bool> ExportLogsZipAsync()
    {
        var history = BeginDeveloperHistory("로그 ZIP 저장", "저장");
        try
        {
            var support = Path.Combine(Path.GetDirectoryName(_preferencesPath ?? LauncherUiPreferences.DefaultPath)!, "support");
            var log = _presentation.LogText;
            var doctor = _lastDoctorReport;
            var zip = await Task.Run(() =>
            {
                var logDirectory = Path.Combine(support, "logs");
                Directory.CreateDirectory(logDirectory);
                var name = $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                File.WriteAllText(Path.Combine(logDirectory, name + ".log"), DiagnosticRedactor.Redact(log));
                var zipPath = Path.Combine(support, name + ".zip");
                ZipFile.CreateFromDirectory(logDirectory, zipPath);
                if (doctor is not null)
                {
                    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
                    using var writer = new StreamWriter(archive.CreateEntry("doctor.json").Open());
                    writer.Write(DiagnosticRedactor.Redact(JsonSerializer.Serialize(doctor, JsonFiles.Options)));
                }
                return zipPath;
            });
            SetStatus("지원 로그 ZIP을 사용자 지원 폴더에 저장했습니다.");
            AppendLog(IsDeveloper ? "지원 로그 저장: " + zip : "지원 로그 ZIP 저장 완료", true);
            history?.Finish(LauncherHistoryResult.Completed);
            return true;
        }
        catch (Exception ex)
        {
            history?.Finish(LauncherHistoryResult.Failed);
            MarkError(ex, "지원 로그 저장 실패", showDialog: IsDeveloper);
            return false;
        }
    }

    private string StorageSummary() => _config.IsManagedDeployment ? "업데이트 서비스에서 관리" : $"임시 파일 {FormatBytes(DirSize(SelectedStatePaths.StagingDir))} / 백업 {FormatBytes(DirSize(SelectedStatePaths.BackupDir))}";
    private static long DirSize(string path) { try { return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0; } catch { return 0; } }
    private static string FormatBytes(long b) { string[] u = { "B", "KB", "MB", "GB", "TB" }; double v = b; var i = 0; while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; } return $"{v:0.##} {u[i]}"; }
    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);


}
