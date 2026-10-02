namespace UeDtLauncher;

internal static class DiskSpace
{
    /// <summary>
    /// Throws when the drive hosting <paramref name="directory"/> has less free space than
    /// <paramref name="requiredBytes"/> plus a 10% safety margin. Skips the check with a warning
    /// when free space cannot be determined (e.g. network shares).
    /// </summary>
    public static void EnsureAvailable(string directory, long requiredBytes, Action<string, string>? warn = null)
    {
        if (requiredBytes <= 0) return;

        long available;
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(directory)) ?? string.Empty;
            if (string.IsNullOrEmpty(root)) return;
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("Disk", $"Free space check skipped for {directory}: {ex.Message}");
            return;
        }

        var required = requiredBytes + requiredBytes / 10;
        if (available < required)
        {
            throw new IOException($"Not enough free disk space on {root}. Required about {FormatBytes(required)}, available {FormatBytes(available)}.");
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
