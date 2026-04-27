using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnUpdateLaunchClicked(object? sender, RoutedEventArgs e)
    {
        await RunAsync(repair: false, launch: true);
    }

    private async void OnRepairClicked(object? sender, RoutedEventArgs e)
    {
        await RunAsync(repair: true, launch: true);
    }

    private async void OnUpdateOnlyClicked(object? sender, RoutedEventArgs e)
    {
        await RunAsync(repair: false, launch: false);
    }

    private async void OnSampleConfigClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = GetConfigPath();
            var config = new LauncherConfig
            {
                ManifestUrl = "https://your-update-server.example.com/files/windows-x64/manifest.json",
                ManifestSignatureUrl = "https://your-update-server.example.com/files/windows-x64/manifest.json.sig",
                ManifestPublicKeyPath = "manifest-public-key.pem",
                InstallDir = "app",
                StagingDir = ".staging",
                BackupDir = ".backup",
                InstalledManifestPath = "installed-manifest.json",
                LaunchAfterUpdate = true,
                HttpTimeoutSeconds = 300,
                LaunchArguments = new[] { "-log" }
            };
            await JsonFiles.WriteAsync(path, config);
            AppendLog($"Sample config written: {path}");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
        }
    }

    private void OnClearLogClicked(object? sender, RoutedEventArgs e)
    {
        LogBox.Text = string.Empty;
    }

    private async Task RunAsync(bool repair, bool launch)
    {
        if (_isRunning)
        {
            AppendLog("Launcher is already running.");
            return;
        }

        _isRunning = true;
        try
        {
            Progress.Value = 0;
            StatusText.Text = "Loading config...";
            var config = await JsonFiles.ReadAsync<LauncherConfig>(GetConfigPath());
            config.RepairMode = repair;
            config.LaunchAfterUpdate = launch;

            var engine = new LauncherEngine(config, progress => Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = $"{progress.Stage}: {progress.Message}";
                if (progress.Percent.HasValue)
                {
                    Progress.Value = progress.Percent.Value;
                }
                AppendLog($"[{progress.Stage}] {progress.Message}");
            }));

            await engine.RunAsync();
            Progress.Value = 100;
            StatusText.Text = "Done";
            AppendLog("Done.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed";
            AppendLog("ERROR: " + ex.Message);
            AppendLog(ex.ToString());
        }
        finally
        {
            _isRunning = false;
        }
    }

    private string GetConfigPath()
    {
        return string.IsNullOrWhiteSpace(ConfigPathBox.Text) ? "launcher.config.json" : ConfigPathBox.Text.Trim();
    }

    private void AppendLog(string message)
    {
        LogBox.Text += $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}";
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }
}
