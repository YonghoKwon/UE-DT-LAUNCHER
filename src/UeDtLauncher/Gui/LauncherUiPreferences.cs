using System.Text.Json;
using System.Diagnostics;
namespace UeDtLauncher.Gui;

public sealed record LauncherUiPreferences(double TextScale = 1, bool HighContrast = false)
{
    public bool HasSeenFirstRunGuide { get; init; }
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
        try { return ReadLatest(path ?? DefaultPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    private static LauncherUiPreferences ReadLatest(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) return new();
            using var reader = new StreamReader(stream);
            var value = JsonSerializer.Deserialize<LauncherUiPreferences>(reader.ReadToEnd(), JsonFiles.Options);
            return value is not null && SupportedScales.Contains(value.TextScale) ? value : new();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException) { return new(); }
    }

    public Task SaveAsync(string? path = null) => UpdateAsync(path, _ => this, forceWrite: true);

    internal static Task<(LauncherUiPreferences Value, bool Changed)> UpdateAsync(
        string? path, Func<LauncherUiPreferences, LauncherUiPreferences> update, bool forceWrite = false) =>
        // File creation, flush-to-disk and replacement all run off the UI thread.
        Task.Run(async () =>
        {
            var target = Path.GetFullPath(path ?? DefaultPath);
            var lockPath = target + ".lock";
            var elapsed = Stopwatch.StartNew();
            SingleInstanceLock? lease;
            while (!SingleInstanceLock.TryAcquire(lockPath, out lease))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(3))
                    throw new IOException("The UI preferences lock could not be acquired.");
                await Task.Delay(25);
            }
            using (lease)
            {
                var latest = ReadLatest(target);
                var value = update(latest);
                if (!SupportedScales.Contains(value.TextScale)) throw new ArgumentOutOfRangeException(nameof(TextScale));
                var changed = value != latest;
                if (changed || forceWrite) await JsonFiles.WriteAsync(target, value);
                return (value, changed);
            }
        });
}
