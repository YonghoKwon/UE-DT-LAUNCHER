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
            ExtractZip(archivePath, destinationDir);
            return;
        }

        if (normalizedFormat is "7z" or "7zip")
        {
            await ExtractWith7ZipAsync(archivePath, destinationDir, log, cancellationToken);
            return;
        }

        throw new NotSupportedException($"Unsupported package format: {normalizedFormat}");
    }

    private static void ExtractZip(string archivePath, string destinationDir)
    {
        // Extract entry by entry so every target path is validated against the destination,
        // instead of trusting archive-supplied paths.
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                if (!string.IsNullOrWhiteSpace(entry.FullName)) Directory.CreateDirectory(SafePath.ResolveInside(destinationDir, entry.FullName));
                continue;
            }

            var targetPath = SafePath.ResolveInsideChecked(destinationDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void MoveExtractedTree(string tempDir, string destinationDir)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tempDir, sourceFile);
            var targetPath = SafePath.ResolveInsideChecked(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Move(sourceFile, targetPath, overwrite: true);
        }
    }

    private static async Task ExtractWith7ZipAsync(string archivePath, string destinationDir, Action<string>? log, CancellationToken cancellationToken)
    {
        var tool = Find7ZipTool();
        if (tool is null)
        {
            throw new FileNotFoundException("7z package extraction requires 7z, 7zz, or 7za in PATH.");
        }

        log?.Invoke($"Extracting with {tool}: {archivePath}");
        // 7z writes archive-supplied paths directly, so extract into a scratch directory first
        // and only move entries that resolve inside the destination.
        var tempDir = destinationDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".extract-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tempDir);
        try
        {
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
            startInfo.ArgumentList.Add("-o" + tempDir);

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

            MoveExtractedTree(tempDir, destinationDir);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Leftover scratch directory is harmless; the next update run recreates a fresh one.
            }
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
