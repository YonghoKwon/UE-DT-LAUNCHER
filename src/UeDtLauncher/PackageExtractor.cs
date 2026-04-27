using System.Diagnostics;
using System.IO.Compression;

namespace UeDtLauncher;

public static class PackageExtractor
{
    public static async Task ExtractAsync(string archivePath, string destinationDir, string? format, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDir);
        var normalizedFormat = (format ?? Path.GetExtension(archivePath).TrimStart('.')).ToLowerInvariant();

        if (normalizedFormat is "zip")
        {
            log?.Invoke($"Extracting ZIP: {archivePath}");
            ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true);
            return;
        }

        if (normalizedFormat is "7z" or "7zip")
        {
            await ExtractWith7ZipAsync(archivePath, destinationDir, log, cancellationToken);
            return;
        }

        throw new NotSupportedException($"Unsupported package format: {normalizedFormat}");
    }

    private static async Task ExtractWith7ZipAsync(string archivePath, string destinationDir, Action<string>? log, CancellationToken cancellationToken)
    {
        var tool = Find7ZipTool();
        if (tool is null)
        {
            throw new FileNotFoundException("7z package extraction requires 7z, 7zz, or 7za in PATH.");
        }

        log?.Invoke($"Extracting with {tool}: {archivePath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-o" + destinationDir);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 7z process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout)) log?.Invoke(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) log?.Invoke(stderr.Trim());

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"7z extraction failed with exit code {process.ExitCode}.");
        }
    }

    private static string? Find7ZipTool()
    {
        foreach (var candidate in OperatingSystem.IsWindows() ? new[] { "7z.exe", "7za.exe", "7zz.exe" } : new[] { "7zz", "7z", "7za" })
        {
            if (CommandExists(candidate)) return candidate;
        }
        return null;
    }

    private static bool CommandExists(string command)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(path => File.Exists(Path.Combine(path, command)));
    }
}
