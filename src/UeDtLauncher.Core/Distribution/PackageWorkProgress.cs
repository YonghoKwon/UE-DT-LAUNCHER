using System.Diagnostics;

namespace UeDtLauncher;

public sealed record PackageWorkProgress(string Phase, long ProcessedBytes, long TotalBytes,
    long EstimatedAdditionalDiskBytes);

/// <summary>Phase changes are immediate; byte-only updates are limited to once per second.</summary>
public sealed class ThrottledPackageProgress(Action<PackageWorkProgress> report, TimeProvider? clock = null)
    : IProgress<PackageWorkProgress>
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private string? phase;
    private long last;

    public void Report(PackageWorkProgress value)
    {
        var now = clock.GetTimestamp();
        if (phase == value.Phase && clock.GetElapsedTime(last, now) < TimeSpan.FromSeconds(1)) return;
        phase = value.Phase;
        last = now;
        report(value);
    }
}

public static class IntakeDiskSpace
{
    public const long ReserveBytes = 64L * 1024 * 1024;

    public static long AvailableBytes(string path)
    {
        var full = Path.GetFullPath(path);
        // On Unix choose the longest matching mount, not necessarily the root filesystem.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives().Where(d => d.IsReady &&
                (string.Equals(full, d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar), comparison) ||
                 full.StartsWith(Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName) + Path.DirectorySeparatorChar, comparison)))
            .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
        return (drive ?? new DriveInfo(Path.GetPathRoot(full)!)).AvailableFreeSpace;
    }

    public static long Estimate(long bytes) => checked(bytes + ReserveBytes);

    public static void Require(string path, long bytes, Func<string, long>? availableBytes = null)
    {
        if ((availableBytes ?? AvailableBytes)(path) < bytes)
            throw new IOException($"Insufficient disk space: estimated additional {bytes} bytes required. This estimate is not a reservation.");
    }
}
