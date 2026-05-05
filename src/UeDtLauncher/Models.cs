using System.Text.Json.Serialization;

namespace UeDtLauncher;

public sealed class LauncherConfig
{
    // Direct manifest mode. Used when CatalogUrl is empty.
    public string ManifestUrl { get; set; } = "http://localhost:8080/manifest.json";
    public string? ManifestSignatureUrl { get; set; }
    public string? ManifestPublicKeyPath { get; set; }

    // Release catalog mode. Recommended for multi-project / multi-version / multi-client deployment.
    public string? CatalogUrl { get; set; }
    public string? CatalogSignatureUrl { get; set; }
    public string? CatalogPublicKeyPath { get; set; }
    public string? ProjectId { get; set; }
    public string ClientProfile { get; set; } = "general"; // general, developer
    public string Environment { get; set; } = "prod"; // prod, dev
    public string Channel { get; set; } = "stable"; // stable, beta, dev
    public string VersionPolicy { get; set; } = "latest"; // latest, exact
    public string? RequestedVersion { get; set; }
    public string TargetPlatform { get; set; } = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    public string InstallDir { get; set; } = "app";
    public string StagingDir { get; set; } = ".staging";
    public string BackupDir { get; set; } = ".backup";
    public string InstalledManifestPath { get; set; } = "installed-manifest.json";
    public bool LaunchAfterUpdate { get; set; } = true;
    public bool RepairMode { get; set; }
    public bool RemoveFilesNotInManifest { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public int HttpTimeoutSeconds { get; set; } = 120;
    public string[]? LaunchArguments { get; set; }
    public List<LauncherPackage> Packages { get; set; } = new();
    public SelfUpdateConfig? SelfUpdate { get; set; }
    public WindowsIntegrationConfig WindowsIntegration { get; set; } = new();
}

public sealed class DistributionCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public string GeneratedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public List<DistributionProject> Projects { get; set; } = new();
}

public sealed class DistributionProject
{
    public string ProjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<DistributionRelease> Releases { get; set; } = new();
}

public sealed class DistributionRelease
{
    public string Version { get; set; } = string.Empty;
    public string Channel { get; set; } = "stable";
    public string Environment { get; set; } = "prod";
    public string Platform { get; set; } = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    public string ManifestUrl { get; set; } = string.Empty;
    public string? ManifestSignatureUrl { get; set; }
    public List<string> AllowedClientProfiles { get; set; } = new() { "general" };
    public bool IsLatest { get; set; }
    public string? Notes { get; set; }
}

public sealed class LauncherPackage
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ExtractTo { get; set; } = ".";
    public bool Required { get; set; } = true;
    public string? Format { get; set; }
}

public sealed class SelfUpdateConfig
{
    public bool Enabled { get; set; }
    public string ManifestUrl { get; set; } = string.Empty;
    public string? ManifestSignatureUrl { get; set; }
    public string? ManifestPublicKeyPath { get; set; }
    public string InstallDir { get; set; } = "launcher-update";
    public string EntryPoint { get; set; } = OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher";
}

public sealed class WindowsIntegrationConfig
{
    public string AppName { get; set; } = "UE Digital Twin";
    public string Publisher { get; set; } = "UE-DT";
    public string? ShortcutName { get; set; } = "UE Digital Twin Launcher";
    public string? IconPath { get; set; }
    public bool CreateDesktopShortcut { get; set; }
    public bool CreateStartMenuShortcut { get; set; }
    public bool RegisterAppEntry { get; set; }
}

public sealed class LauncherManifest
{
    public string AppId { get; set; } = "ue-dt-app";
    public string Version { get; set; } = "0.0.0";
    public string Channel { get; set; } = "stable";
    public string Platform { get; set; } = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";
    public string EntryPoint { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Url { get; set; }
    public bool Executable { get; set; }
}

public sealed class UpdatePlan
{
    public List<ManifestFile> DownloadOrRepair { get; } = new();
    public List<string> Remove { get; } = new();

    [JsonIgnore]
    public bool HasChanges => DownloadOrRepair.Count > 0 || Remove.Count > 0;
}

public sealed record LauncherProgress(string Stage, string Message, double? Percent = null);
