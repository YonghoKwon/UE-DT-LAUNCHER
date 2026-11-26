using System.Text.Json;
namespace UeDtLauncher.Gui;

public sealed record LauncherUiPreferences(double TextScale = 1, bool HighContrast = false)
{
    public static readonly double[] SupportedScales = [1, 1.25, 1.5, 2];
    internal const string DirectoryOverride = "UE_DT_UI_PREFERENCES_DIR";
    private const string FileName = "ui-preferences.json";

    public static string DefaultPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable(DirectoryOverride) is { } directory)
            {
                if (!Path.IsPathFullyQualified(directory))
                    throw new IOException("The isolated UI preferences directory must be an absolute path.");
                return Path.Combine(directory, FileName);
            }

            var userConfigDirectory = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(userConfigDirectory, "UE-DT Launcher", FileName);
        }
    }
    public static LauncherUiPreferences Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            var value = JsonSerializer.Deserialize<LauncherUiPreferences>(File.ReadAllText(path), JsonFiles.Options);
            return value is not null && SupportedScales.Contains(value.TextScale) ? value : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public Task SaveAsync(string? path = null)
    {
        if (!SupportedScales.Contains(TextScale)) throw new ArgumentOutOfRangeException(nameof(TextScale));
        return JsonFiles.WriteAsync(path ?? DefaultPath, this);
    }
}
