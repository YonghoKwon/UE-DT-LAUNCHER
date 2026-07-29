using System.Text.RegularExpressions;

namespace UeDtLauncher;

public sealed record ProjectStatePaths(
    string RootDir,
    string StagingDir,
    string BackupDir,
    string InstalledManifestPath,
    string InstallStatePath,
    string AppPidPath,
    string UpdateLockPath);

public static partial class LauncherPaths
{
    public static async Task<LauncherConfig> LoadResolvedAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        var config = await JsonFiles.ReadAsync<LauncherConfig>(fullConfigPath, cancellationToken);
        var legacy = LegacyStatePaths.From(config, fullConfigPath);

        ResolveInPlace(config, fullConfigPath);
        MigrateLegacySingleProjectState(config, legacy);
        return config;
    }

    public static void ResolveInPlace(
        LauncherConfig config,
        string configPath,
        string? installPathOverride = null)
    {
        var fullConfigPath = Path.GetFullPath(configPath);
        config.InstallDir = ResolveConfigRelative(fullConfigPath, installPathOverride ?? config.InstallDir);
        config.LogDir = ResolveConfigRelative(fullConfigPath, config.LogDir);
        config.ManifestPublicKeyPath = ResolveOptionalConfigRelative(fullConfigPath, config.ManifestPublicKeyPath);
        config.CatalogPublicKeyPath = ResolveOptionalConfigRelative(fullConfigPath, config.CatalogPublicKeyPath);

        if (config.WindowsIntegration.IconPath is not null)
        {
            config.WindowsIntegration.IconPath = ResolveConfigRelative(fullConfigPath, config.WindowsIntegration.IconPath);
        }

        if (config.SelfUpdate is not null)
        {
            config.SelfUpdate.InstallDir = ResolveConfigRelative(fullConfigPath, config.SelfUpdate.InstallDir);
            config.SelfUpdate.ManifestPublicKeyPath = ResolveOptionalConfigRelative(
                fullConfigPath,
                config.SelfUpdate.ManifestPublicKeyPath);
        }

        var state = For(config, fullConfigPath);
        config.StagingDir = state.StagingDir;
        config.BackupDir = state.BackupDir;
        config.InstalledManifestPath = state.InstalledManifestPath;
        config.InstallStatePath = state.InstallStatePath;
        config.AppPidPath = state.AppPidPath;
    }

    public static ProjectStatePaths For(
        LauncherConfig config,
        string configPath,
        string? projectId = null,
        string? targetPlatform = null)
    {
        var stateKey = ValidateStateSegment(projectId ?? config.ProjectId ?? "default", "projectId");
        var platformKey = ValidateStateSegment(targetPlatform ?? config.TargetPlatform, "targetPlatform");
        var stateRoot = ResolveConfigRelative(configPath, config.StateRootDir);
        var root = Path.Combine(stateRoot, stateKey, platformKey);

        return new ProjectStatePaths(
            root,
            Path.Combine(root, "staging"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "installed-manifest.json"),
            Path.Combine(root, "install-state.json"),
            Path.Combine(root, "app.pid"),
            Path.Combine(root, "update.lock"));
    }

    public static string ResolveConfigRelative(string configPath, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Configured path must not be empty.");
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var fullConfigPath = Path.GetFullPath(configPath);
        var configDirectory = Path.GetDirectoryName(fullConfigPath)
                              ?? throw new InvalidOperationException($"Could not resolve the config directory: {configPath}");
        return Path.GetFullPath(Path.Combine(configDirectory, path));
    }

    public static string UpdateLockPath(LauncherConfig config) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))
            ?? throw new InvalidOperationException("Could not resolve the project state directory."),
            "update.lock");

    private static string? ResolveOptionalConfigRelative(string configPath, string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : ResolveConfigRelative(configPath, path);

    private static string ValidateStateSegment(string value, string fieldName)
    {
        if (!StateSegmentPattern().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{fieldName} must start with a letter or digit and contain only letters, digits, '.', '_' or '-': {value}");
        }

        return value;
    }

    private static void MigrateLegacySingleProjectState(LauncherConfig config, LegacyStatePaths legacy)
    {
        if (config.Projects.Count > 1)
        {
            return;
        }

        CopyFileIfTargetMissing(legacy.InstalledManifestPath, config.InstalledManifestPath);
        CopyFileIfTargetMissing(legacy.InstallStatePath, config.InstallStatePath);
        CopyDirectoryIfTargetEmpty(legacy.BackupDir, config.BackupDir);
    }

    private static void CopyFileIfTargetMissing(string source, string target)
    {
        if (!File.Exists(source) || File.Exists(target) || SamePath(source, target))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target, overwrite: false);
    }

    private static void CopyDirectoryIfTargetEmpty(string source, string target)
    {
        if (!Directory.Exists(source) || SamePath(source, target))
        {
            return;
        }

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var targetFile = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: false);
        }
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private sealed record LegacyStatePaths(
        string BackupDir,
        string InstalledManifestPath,
        string InstallStatePath)
    {
        public static LegacyStatePaths From(LauncherConfig config, string configPath) =>
            new(
                ResolveConfigRelative(configPath, config.BackupDir),
                ResolveConfigRelative(configPath, config.InstalledManifestPath),
                ResolveConfigRelative(configPath, config.InstallStatePath));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex StateSegmentPattern();
}
