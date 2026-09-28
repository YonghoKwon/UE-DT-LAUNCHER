using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace UeDtLauncher;

public static class WindowsIntegration
{
    public static void Apply(LauncherConfig config, string launcherTargetPath, Action<string, string, double?>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            log?.Invoke("Integration", "Windows integration skipped on non-Windows OS.", null);
            return;
        }

        var integration = config.WindowsIntegration;
        var shortcutName = string.IsNullOrWhiteSpace(integration.ShortcutName) ? integration.AppName : integration.ShortcutName!;
        if (shortcutName != Path.GetFileName(shortcutName) || shortcutName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Shortcut name must be a file name, not a path.");
        var manifest = JsonFiles.ReadAsync<LauncherManifest>(config.InstalledManifestPath).GetAwaiter().GetResult();
        var version = config.SelectedRelease?.Version ?? manifest.Version;
        ReleaseSidecar.Segment(version);
        shortcutName += " - " + version;
        var profileRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UE-DT Launcher", "shortcuts", RuntimeStore.InstallationId(config));
        var profile = CreateLaunchProfile(config, version, profileRoot);
        var arguments = "run --config " + NativeProcessFamily.QuoteWindows(profile);

        if (integration.CreateDesktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateLauncherShortcut(Path.Combine(desktop, shortcutName + ".lnk"), launcherTargetPath, arguments, integration.IconPath);
            log?.Invoke("Integration", "Desktop shortcut created.", null);
        }

        if (integration.CreateStartMenuShortcut)
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            var folder = Path.Combine(startMenu, "Programs", integration.Publisher);
            Directory.CreateDirectory(folder);
            CreateLauncherShortcut(Path.Combine(folder, shortcutName + ".lnk"), launcherTargetPath, arguments, integration.IconPath);
            log?.Invoke("Integration", "Start menu shortcut created.", null);
        }

        if (integration.RegisterAppEntry)
        {
            var registrationPath = Path.Combine(AppContext.BaseDirectory, "windows-app-registration.txt");
            File.WriteAllText(registrationPath,
                $"AppName={integration.AppName}{Environment.NewLine}" +
                $"Publisher={integration.Publisher}{Environment.NewLine}" +
                $"InstallLocation={Path.GetFullPath(config.InstallDir)}{Environment.NewLine}" +
                $"Launcher={launcherTargetPath}{Environment.NewLine}");
            log?.Invoke("Integration", "Windows app registration descriptor written. Use an installer/MSIX for production registry registration.", null);
        }
    }

    internal static string CreateLaunchProfile(LauncherConfig config, string version, string root)
    {
        ReleaseSidecar.Segment(version);
        var clone = JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(config, JsonFiles.Options), JsonFiles.Options)!;
        clone.InstallDir = config.VersionedInstallRoot ?? config.InstallDir;
        clone.VersionPolicy = "exact"; clone.RequestedVersion = version;
        clone.WindowsIntegration = new();
        var path = Path.Combine(root, version + ".json");
        Directory.CreateDirectory(root);
        if (!File.Exists(path)) JsonFiles.WriteAsync(path, clone).GetAwaiter().GetResult();
        return path;
    }

    [SupportedOSPlatform("windows")]
    internal static void CreateLauncherShortcut(string shortcutPath, string targetPath, string arguments, string? iconPath)
    {
        // Never overwrite or delete an existing user/legacy shortcut.
        if (File.Exists(shortcutPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(Path.GetFullPath(shortcutPath));
            shortcut.TargetPath = Path.GetFullPath(targetPath);
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrWhiteSpace(iconPath)) shortcut.IconLocation = Path.GetFullPath(iconPath);
            shortcut.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
