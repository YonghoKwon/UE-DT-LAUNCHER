using System.Diagnostics;

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

        if (integration.CreateDesktopShortcut)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateUrlShortcut(Path.Combine(desktop, shortcutName + ".url"), launcherTargetPath, integration.IconPath);
            log?.Invoke("Integration", "Desktop shortcut created.", null);
        }

        if (integration.CreateStartMenuShortcut)
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            var folder = Path.Combine(startMenu, "Programs", integration.Publisher);
            Directory.CreateDirectory(folder);
            CreateUrlShortcut(Path.Combine(folder, shortcutName + ".url"), launcherTargetPath, integration.IconPath);
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

    private static void CreateUrlShortcut(string shortcutPath, string targetPath, string? iconPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var lines = new List<string>
        {
            "[InternetShortcut]",
            "URL=file:///" + Path.GetFullPath(targetPath).Replace('\\', '/'),
            "IconIndex=0"
        };

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            lines.Add("IconFile=" + Path.GetFullPath(iconPath));
        }
        else if (File.Exists(targetPath))
        {
            lines.Add("IconFile=" + Path.GetFullPath(targetPath));
        }

        File.WriteAllLines(shortcutPath, lines);
    }
}
