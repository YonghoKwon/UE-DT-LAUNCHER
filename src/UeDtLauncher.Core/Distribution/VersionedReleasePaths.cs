namespace UeDtLauncher;
public static class VersionedReleasePaths
{
    public static void Bind(LauncherConfig config, ReleaseSelection selection)
    {
        selection.Validate();
        if (config.VersionedInstallRoot is null) throw new InvalidOperationException("Versioned install root must be resolved first.");
        config.SelectedRelease = selection;
        config.InstallDir = SafePath.ResolveInside(config.VersionedInstallRoot, selection.ReleaseId);
        var state = SafePath.ResolveInside(Path.GetFullPath(config.StateRootDir), selection.ReleaseId);
        config.StagingDir = Path.Combine(state, "staging"); config.BackupDir = Path.Combine(state, "backups");
        config.InstalledManifestPath = Path.Combine(state, "installed-manifest.json");
        config.InstallStatePath = Path.Combine(state, "install-state.json"); config.AppPidPath = Path.Combine(state, "app.pid");
    }
}
