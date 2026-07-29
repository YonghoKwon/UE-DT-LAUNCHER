using System.Diagnostics;

namespace UeDtLauncher;

public static class SelfUpdateManager
{
    private const string PendingFileName = "self-update-pending.json";

    private static string LauncherDirectory => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

    public static string PendingFilePath => Path.Combine(LauncherDirectory, PendingFileName);

    /// <summary>
    /// Called first thing on startup. When a staged self-update is pending, spawns a swap script
    /// that waits for this process to exit, copies the staged files over the launcher directory,
    /// and restarts the launcher with the original arguments. Returns true when the caller should exit.
    /// </summary>
    public static bool TryApplyPendingUpdate(string[] args)
    {
        var pendingPath = PendingFilePath;
        if (!File.Exists(pendingPath)) return false;

        try
        {
            var pending = JsonFiles.ReadAsync<SelfUpdatePending>(pendingPath).GetAwaiter().GetResult();
            var launcherExe = Environment.ProcessPath;
            if (launcherExe is null || !Directory.Exists(pending.StagedDir))
            {
                File.Delete(pendingPath);
                return false;
            }

            var script = OperatingSystem.IsWindows()
                ? BuildWindowsSwapScript(LauncherDirectory, pending.StagedDir, launcherExe, Environment.ProcessId, pendingPath, args)
                : BuildUnixSwapScript(LauncherDirectory, pending.StagedDir, launcherExe, Environment.ProcessId, pendingPath, args);

            var scriptPath = Path.Combine(Path.GetTempPath(), "uedt-self-update-" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));
            File.WriteAllText(scriptPath, script);

            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo { FileName = "cmd.exe", ArgumentList = { "/c", scriptPath }, UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { scriptPath }, UseShellExecute = false };

            Process.Start(startInfo);
            Console.WriteLine("Applying staged launcher self-update; restarting...");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Self-update apply failed, continuing with the current launcher: " + ex.Message);
            try
            {
                File.Delete(pendingPath); // avoid a restart loop on a broken pending state
            }
            catch (IOException)
            {
            }

            return false;
        }
    }

    internal static string BuildWindowsSwapScript(string launcherDir, string stagedDir, string launcherExe, int pid, string pendingPath, string[] args)
    {
        var safeLauncherDir = WindowsBatchPath(launcherDir);
        var safeStagedDir = WindowsBatchPath(stagedDir);
        var safeLauncherExe = WindowsBatchPath(launcherExe);
        var safePendingPath = WindowsBatchPath(pendingPath);
        var restartArgs = string.Join(" ", args.Select(WindowsBatchArgument));
        return string.Join("\r\n",
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 (",
            "  timeout /T 1 /NOBREAK >NUL",
            "  goto wait",
            ")",
            $"xcopy /E /Y /I \"{safeStagedDir}\\*\" \"{safeLauncherDir}\\\" >NUL",
            "if errorlevel 1 goto copy_failed",
            $"del /F /Q \"{safePendingPath}\"",
            $"start \"\" \"{safeLauncherExe}\" {restartArgs}",
            "goto cleanup",
            ":copy_failed",
            "echo Launcher self-update copy failed. 1>&2",
            $"del /F /Q \"{safePendingPath}\"",
            $"start \"\" \"{safeLauncherExe}\" {restartArgs}",
            ":cleanup",
            "del \"%~f0\"",
            "");
    }

    internal static string BuildUnixSwapScript(string launcherDir, string stagedDir, string launcherExe, int pid, string pendingPath, string[] args)
    {
        var restartArgs = string.Join(" ", args.Select(ShellQuote));
        return string.Join("\n",
            "#!/bin/sh",
            $"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done",
            $"if cp -rf {ShellQuote(stagedDir + "/.")} {ShellQuote(launcherDir + "/")} && chmod +x {ShellQuote(launcherExe)}; then",
            $"  rm -f {ShellQuote(pendingPath)}",
            "else",
            "  echo 'Launcher self-update copy failed.' >&2",
            $"  rm -f {ShellQuote(pendingPath)}",
            "fi",
            $"{ShellQuote(launcherExe)} {restartArgs} >/dev/null 2>&1 &",
            "rm -f \"$0\"",
            "");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string WindowsBatchPath(string value)
    {
        if (value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0)
        {
            throw new InvalidOperationException("Self-update paths contain characters that are unsafe in a Windows batch file.");
        }

        return value;
    }

    private static string WindowsBatchArgument(string value)
    {
        if (value.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0)
        {
            throw new InvalidOperationException("Launcher arguments contain characters that are unsafe in a Windows batch file.");
        }

        return "\"" + value + "\"";
    }

    public static async Task PrepareAsync(SelfUpdateConfig selfUpdate, LauncherConfig parentConfig, Action<string, string, double?>? log = null, CancellationToken cancellationToken = default)
    {
        if (!selfUpdate.Enabled || string.IsNullOrWhiteSpace(selfUpdate.ManifestUrl))
        {
            return;
        }

        var updateConfig = BuildUpdateConfig(selfUpdate, parentConfig);

        log?.Invoke("SelfUpdate", "Checking launcher self update manifest.", null);
        using (var updateEngine = new LauncherEngine(updateConfig, progress => log?.Invoke(progress.Stage, progress.Message, progress.Percent)))
        {
            await updateEngine.RunAsync(cancellationToken);
        }

        var newEntry = SafePath.ResolveInside(selfUpdate.InstallDir, selfUpdate.EntryPoint);
        if (File.Exists(newEntry))
        {
            if (selfUpdate.AutoApply && Environment.ProcessPath is not null)
            {
                await JsonFiles.WriteAsync(PendingFilePath, new SelfUpdatePending
                {
                    StagedDir = Path.GetFullPath(selfUpdate.InstallDir),
                    EntryPoint = selfUpdate.EntryPoint
                }, cancellationToken);
                log?.Invoke("SelfUpdate", "Launcher self update staged. It will be applied automatically on the next start.", null);
            }
            else
            {
                var notePath = Path.Combine(selfUpdate.InstallDir, "SELF_UPDATE_READY.txt");
                await File.WriteAllTextAsync(notePath,
                    "A launcher update has been downloaded and verified." + Environment.NewLine +
                    "Close the running launcher and replace the current launcher binary with the prepared file manually or via your installer." + Environment.NewLine +
                    "Prepared entry: " + newEntry + Environment.NewLine +
                    "Tip: set selfUpdate.autoApply=true in launcher.config.json to apply this automatically on next start." + Environment.NewLine,
                    cancellationToken);
                log?.Invoke("SelfUpdate", "Launcher self update prepared in: " + selfUpdate.InstallDir, null);
            }
        }
        else
        {
            log?.Invoke("SelfUpdate", "Self update manifest processed, but configured entry point was not found: " + newEntry, null);
        }
    }

    internal static LauncherConfig BuildUpdateConfig(
        SelfUpdateConfig selfUpdate,
        LauncherConfig parentConfig) =>
        new()
        {
            ManifestUrl = selfUpdate.ManifestUrl,
            ManifestSignatureUrl = selfUpdate.ManifestSignatureUrl,
            ManifestPublicKeyPath = selfUpdate.ManifestPublicKeyPath,
            RequireSignedManifests = parentConfig.RequireSignedManifests,
            ProjectId = "launcher-self-update",
            TargetPlatform = parentConfig.TargetPlatform,
            InstallDir = selfUpdate.InstallDir,
            StagingDir = Path.Combine(parentConfig.StagingDir, "self-update"),
            BackupDir = Path.Combine(parentConfig.BackupDir, "self-update"),
            InstalledManifestPath = Path.Combine(selfUpdate.InstallDir, "installed-manifest.json"),
            InstallStatePath = Path.Combine(selfUpdate.InstallDir, "install-state.json"),
            AppPidPath = Path.Combine(selfUpdate.InstallDir, "app.pid"),
            LaunchAfterUpdate = false,
            RepairMode = false,
            RemoveFilesNotInManifest = false,
            MaxRetryCount = parentConfig.MaxRetryCount,
            HttpTimeoutSeconds = parentConfig.HttpTimeoutSeconds
        };
}
