namespace UeDtLauncher;

public static class SelfUpdateManager
{
    public static async Task PrepareAsync(SelfUpdateConfig selfUpdate, LauncherConfig parentConfig, Action<string, string, double?>? log = null, CancellationToken cancellationToken = default)
    {
        if (!selfUpdate.Enabled || string.IsNullOrWhiteSpace(selfUpdate.ManifestUrl))
        {
            return;
        }

        var updateConfig = new LauncherConfig
        {
            ManifestUrl = selfUpdate.ManifestUrl,
            ManifestSignatureUrl = selfUpdate.ManifestSignatureUrl,
            ManifestPublicKeyPath = selfUpdate.ManifestPublicKeyPath,
            InstallDir = selfUpdate.InstallDir,
            StagingDir = Path.Combine(parentConfig.StagingDir, "self-update"),
            BackupDir = Path.Combine(parentConfig.BackupDir, "self-update"),
            InstalledManifestPath = Path.Combine(selfUpdate.InstallDir, "installed-manifest.json"),
            LaunchAfterUpdate = false,
            RepairMode = false,
            RemoveFilesNotInManifest = false,
            MaxRetryCount = parentConfig.MaxRetryCount,
            HttpTimeoutSeconds = parentConfig.HttpTimeoutSeconds
        };

        log?.Invoke("SelfUpdate", "Checking launcher self update manifest.", null);
        using (var updateEngine = new LauncherEngine(updateConfig, progress => log?.Invoke(progress.Stage, progress.Message, progress.Percent)))
        {
            await updateEngine.RunAsync(cancellationToken);
        }

        var newEntry = SafePath.ResolveInside(selfUpdate.InstallDir, selfUpdate.EntryPoint);
        if (File.Exists(newEntry))
        {
            var notePath = Path.Combine(selfUpdate.InstallDir, "SELF_UPDATE_READY.txt");
            await File.WriteAllTextAsync(notePath,
                "A launcher update has been downloaded and verified." + Environment.NewLine +
                "Close the running launcher and replace the current launcher binary with the prepared file manually or via your installer." + Environment.NewLine +
                "Prepared entry: " + newEntry + Environment.NewLine,
                cancellationToken);
            log?.Invoke("SelfUpdate", "Launcher self update prepared in: " + selfUpdate.InstallDir, null);
        }
        else
        {
            log?.Invoke("SelfUpdate", "Self update manifest processed, but configured entry point was not found: " + newEntry, null);
        }
    }
}
