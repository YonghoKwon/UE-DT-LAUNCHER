using System.Text.Json;
namespace UeDtLauncher.Gui;

public sealed record LauncherUiPreferences(double TextScale = 1, bool HighContrast = false)
{
    public static readonly double[] SupportedScales = [1, 1.25, 1.5, 2];
    public static string DefaultPath => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "UE-DT Launcher", "ui-preferences.json");
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
    public void Save(string? path = null)
    {
        if (!SupportedScales.Contains(TextScale)) throw new ArgumentOutOfRangeException(nameof(TextScale));
        JsonFiles.WriteAsync(path ?? DefaultPath, this).GetAwaiter().GetResult();
    }
}
