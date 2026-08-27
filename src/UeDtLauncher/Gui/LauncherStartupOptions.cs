namespace UeDtLauncher.Gui;

public enum LauncherConfigSource
{
    Explicit,
    Managed,
    Portable,
    Missing
}

public sealed record LauncherStartupOptions(
    string ConfigPath,
    LauncherConfigSource ConfigSource,
    bool ConfigExists)
{
    public static LauncherStartupOptions Discover(
        IReadOnlyList<string> args,
        ManagedLauncherPathLayout? managedLayout = null,
        string? baseDirectory = null,
        string? currentDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        currentDirectory ??= Environment.CurrentDirectory;

        var explicitPath = ReadConfigArgument(args);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = Path.GetFullPath(explicitPath, currentDirectory);
            return new LauncherStartupOptions(resolved, LauncherConfigSource.Explicit, File.Exists(resolved));
        }

        var layout = managedLayout ?? ManagedLauncherPathLayout.Current();
        var managedPath = Path.Combine(layout.ConfigRoot, "launcher.config.json");
        if (File.Exists(managedPath))
            return new LauncherStartupOptions(Path.GetFullPath(managedPath), LauncherConfigSource.Managed, true);

        var portablePath = Path.Combine(baseDirectory, "launcher.config.json");
        if (File.Exists(portablePath))
            return new LauncherStartupOptions(Path.GetFullPath(portablePath), LauncherConfigSource.Portable, true);

        return new LauncherStartupOptions(Path.GetFullPath(portablePath), LauncherConfigSource.Missing, false);
    }

    private static string? ReadConfigArgument(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            if (value.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                return value["--config=".Length..].Trim();
            if (!value.Equals("--config", StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("--config requires a path.");
            return args[index + 1].Trim();
        }

        return null;
    }
}
