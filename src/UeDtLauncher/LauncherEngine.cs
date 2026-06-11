using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;

namespace UeDtLauncher;

public sealed class LauncherEngine : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Action<LauncherProgress>? _progress;
    private readonly FileLogger? _fileLogger;

    public LauncherEngine(LauncherConfig config, Action<LauncherProgress>? progress = null, FileLogger? fileLogger = null)
    {
        _config = config;
        _progress = progress;
        _fileLogger = fileLogger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds))
        };
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var instanceLock = SingleInstanceLock.Acquire(SingleInstanceLock.LockPathFor(_config.InstallDir));

        Directory.CreateDirectory(_config.InstallDir);
        Directory.CreateDirectory(_config.StagingDir);
        Directory.CreateDirectory(_config.BackupDir);

        Log("Manifest", "Downloading remote manifest...", 5);
        var (remoteManifest, manifestJson) = await DownloadManifestAsync(cancellationToken);
        ValidateManifest(remoteManifest);

        Log("Manifest", $"App: {remoteManifest.AppId} / Version: {remoteManifest.Version} / Platform: {remoteManifest.Platform}", 10);
        var localManifest = await TryLoadLocalManifestAsync(cancellationToken);

        Log("Plan", "Building update plan...", 15);
        var plan = await BuildPlanAsync(remoteManifest, localManifest, cancellationToken);
        Log("Plan", $"Download/repair: {plan.DownloadOrRepair.Count}, Remove: {plan.Remove.Count}", 20);

        if (!plan.HasChanges)
        {
            Log("Plan", "Already up to date.", 35);
            if (!File.Exists(_config.InstallStatePath))
            {
                await WriteInstallStateAsync(remoteManifest, manifestJson, backupRoot: null, cancellationToken);
            }
        }
        else
        {
            var requiredBytes = plan.DownloadOrRepair.Sum(file => Math.Max(0, file.Size));
            DiskSpace.EnsureAvailable(_config.StagingDir, requiredBytes, (stage, message) => Log(stage, message));
            DiskSpace.EnsureAvailable(_config.InstallDir, requiredBytes, (stage, message) => Log(stage, message));

            Log("Download", "Downloading changed files to staging...", 35);
            await PrepareStagingAsync(plan, remoteManifest, cancellationToken);

            Log("Apply", "Applying update with backup...", 70);
            var backupRoot = await ApplyUpdateAsync(plan, localManifest, remoteManifest, cancellationToken);

            Log("Manifest", "Writing installed manifest...", 80);
            await JsonFiles.WriteAsync(_config.InstalledManifestPath, remoteManifest, cancellationToken);
            await WriteInstallStateAsync(remoteManifest, manifestJson, backupRoot, cancellationToken);
            BackupManager.Prune(_config.BackupDir, _config.MaxBackupCount, message => Log("Backup", message));
        }

        if (_config.Packages.Count > 0)
        {
            Log("Package", "Processing package updates...", 84);
            await ProcessPackagesAsync(cancellationToken);
        }

        if (_config.WindowsIntegration.CreateDesktopShortcut || _config.WindowsIntegration.CreateStartMenuShortcut || _config.WindowsIntegration.RegisterAppEntry)
        {
            Log("Integration", "Applying Windows integration settings...", 90);
            WindowsIntegration.Apply(_config, GetEntryPointPath(remoteManifest), Log);
        }

        if (_config.SelfUpdate?.Enabled == true)
        {
            Log("SelfUpdate", "Preparing launcher self update if available...", 94);
            await SelfUpdateManager.PrepareAsync(_config.SelfUpdate, _config, Log, cancellationToken);
        }

        if (_config.LaunchAfterUpdate)
        {
            Log("Launch", "Launching application...", 98);
            Launch(remoteManifest);
            Log("Complete", "Application launched.", 100);
        }
        else
        {
            Log("Complete", "Update completed. Launch skipped by configuration.", 100);
        }
    }

    private void Log(string stage, string message, double? percent = null)
    {
        Console.WriteLine(percent.HasValue ? $"[{stage}] {message} ({percent:0}%)" : $"[{stage}] {message}");
        _fileLogger?.Log(stage, message);
        _progress?.Invoke(new LauncherProgress(stage, message, percent));
    }

    private async Task WriteInstallStateAsync(LauncherManifest manifest, string manifestJson, string? backupRoot, CancellationToken cancellationToken)
    {
        var state = new InstallState
        {
            Version = manifest.Version,
            Channel = manifest.Channel,
            Environment = _config.Environment,
            Platform = manifest.Platform,
            ManifestSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant(),
            LastBackupRoot = backupRoot
        };
        await JsonFiles.WriteAsync(_config.InstallStatePath, state, cancellationToken);
    }

    private async Task<(LauncherManifest Manifest, string Json)> DownloadManifestAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(_config.ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var signatureVerified = await ManifestSignatureVerifier.VerifyIfConfiguredAsync(json, _config, _httpClient, cancellationToken);
        if (!signatureVerified)
        {
            if (_config.RequireSignedManifests)
            {
                throw new InvalidOperationException("requireSignedManifests is enabled, but manifestSignatureUrl or manifestPublicKeyPath is not configured.");
            }

            Log("Security", "WARNING: manifest signature verification skipped (no signature URL or public key configured).");
        }

        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, JsonFiles.Options) ?? throw new InvalidOperationException("Remote manifest JSON was empty or invalid.");
        return (manifest, json);
    }

    private async Task<LauncherManifest?> TryLoadLocalManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_config.InstalledManifestPath)) return null;
        try
        {
            return await JsonFiles.ReadAsync<LauncherManifest>(_config.InstalledManifestPath, cancellationToken);
        }
        catch (Exception ex)
        {
            Log("Repair", $"Local manifest is unreadable, repair scan will be used. Reason: {ex.Message}");
            return null;
        }
    }

    private async Task<UpdatePlan> BuildPlanAsync(LauncherManifest remote, LauncherManifest? local, CancellationToken cancellationToken)
    {
        var plan = new UpdatePlan();
        var localByPath = local?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var remoteFile in remote.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installedPath = SafePath.ResolveInside(_config.InstallDir, remoteFile.Path);
            var shouldDownload = _config.RepairMode || !File.Exists(installedPath);

            if (!shouldDownload && localByPath.TryGetValue(remoteFile.Path, out var localFile))
            {
                shouldDownload = !string.Equals(localFile.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase) || localFile.Size != remoteFile.Size;
            }

            if (!shouldDownload)
            {
                shouldDownload = !await Hashing.Sha256MatchesAsync(installedPath, remoteFile.Sha256, cancellationToken);
            }

            if (shouldDownload) plan.DownloadOrRepair.Add(remoteFile);
        }

        if (_config.RemoveFilesNotInManifest && local is not null)
        {
            var remotePaths = remote.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var localFile in local.Files)
            {
                if (!remotePaths.Contains(localFile.Path)) plan.Remove.Add(localFile.Path);
            }
        }

        return plan;
    }

    private async Task PrepareStagingAsync(UpdatePlan plan, LauncherManifest remote, CancellationToken cancellationToken)
    {
        if (Directory.Exists(_config.StagingDir)) Directory.Delete(_config.StagingDir, recursive: true);
        Directory.CreateDirectory(_config.StagingDir);

        var total = Math.Max(1, plan.DownloadOrRepair.Count);
        for (var index = 0; index < plan.DownloadOrRepair.Count; index++)
        {
            var file = plan.DownloadOrRepair[index];
            cancellationToken.ThrowIfCancellationRequested();
            var stagingPath = SafePath.ResolveInside(_config.StagingDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            var downloadUri = ResolveDownloadUri(remote, file);
            Log("Download", file.Path, 35 + (index / (double)total) * 30);
            await DownloadWithRetryAsync(downloadUri, stagingPath, file, cancellationToken);
        }
    }

    private Uri ResolveDownloadUri(LauncherManifest manifest, ManifestFile file)
    {
        if (Uri.TryCreate(file.Url, UriKind.Absolute, out var absolute)) return absolute;
        var baseUrl = manifest.BaseUrl ?? throw new InvalidOperationException($"File URL is relative but manifest.baseUrl is missing: {file.Path}");
        return new Uri(baseUrl.TrimEnd('/') + "/" + (file.Url ?? file.Path).TrimStart('/'));
    }

    private async Task DownloadWithRetryAsync(Uri uri, string targetPath, ManifestFile file, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, _config.MaxRetryCount);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadFileAsync(uri, targetPath, file.Size, cancellationToken);
                if (!await Hashing.Sha256MatchesAsync(targetPath, file.Sha256, cancellationToken))
                    throw new IOException($"SHA-256 mismatch after download: {file.Path}");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (!IsTransientDownloadError(ex))
                {
                    throw new InvalidOperationException($"Download failed (not retryable): {file.Path}: {ex.Message}", ex);
                }

                if (attempt >= maxAttempts) break;

                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
                Log("Retry", $"{file.Path}: retry {attempt}/{maxAttempts} in {delay.TotalSeconds:0.#}s: {ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Download failed: {file.Path}", lastError);
    }

    internal static bool IsTransientDownloadError(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null) return true; // DNS/connection-level failure
            var statusCode = (int)http.StatusCode.Value;
            return statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }

        // TaskCanceledException without external cancellation means the HttpClient timeout fired.
        return ex is IOException or System.Net.Sockets.SocketException or TaskCanceledException;
    }

    private async Task DownloadFileAsync(Uri uri, string targetPath, long expectedSize, CancellationToken cancellationToken)
    {
        var tempPath = targetPath + ".download";
        var existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existingLength > 0 && existingLength < expectedSize) request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existingLength > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existingLength = 0;
            await DeleteFileWithRetryAsync(tempPath, cancellationToken);
        }
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(tempPath, existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }

        var actualSize = new FileInfo(tempPath).Length;
        if (expectedSize > 0 && actualSize != expectedSize) throw new IOException($"Size mismatch. Expected {expectedSize}, actual {actualSize}.");
        await MoveFileWithRetryAsync(tempPath, targetPath, cancellationToken);
    }

    private static async Task MoveFileWithRetryAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }

        throw new IOException($"Failed to move downloaded file from '{sourcePath}' to '{targetPath}'.", lastError);
    }

    private static async Task DeleteFileWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }

        throw new IOException($"Failed to delete file '{path}'.", lastError);
    }

    private async Task<string> ApplyUpdateAsync(UpdatePlan plan, LauncherManifest? localManifest, LauncherManifest remoteManifest, CancellationToken cancellationToken)
    {
        var backupRoot = BackupManager.CreateBackupRoot(_config.BackupDir);
        var addedPaths = new List<string>();

        // Capture pre-update metadata first: the installed manifest/state files still describe
        // the previous version at this point.
        await BackupManager.WriteBackupMetadataAsync(backupRoot, new BackupInfo
        {
            PreviousVersion = localManifest?.Version,
            NewVersion = remoteManifest.Version
        }, _config.InstalledManifestPath, _config.InstallStatePath, cancellationToken);

        try
        {
            foreach (var relativePath in plan.Remove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, relativePath);
                if (File.Exists(installedPath))
                {
                    BackupFile(installedPath, SafePath.ResolveInside(backupRoot, relativePath));
                    File.Delete(installedPath);
                }
            }

            foreach (var file in plan.DownloadOrRepair)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = SafePath.ResolveInside(_config.StagingDir, file.Path);
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, file.Path);
                var backupPath = SafePath.ResolveInside(backupRoot, file.Path);
                if (File.Exists(installedPath)) BackupFile(installedPath, backupPath);
                else addedPaths.Add(file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
                File.Move(stagingPath, installedPath, overwrite: true);
                TryMarkExecutable(installedPath, file.Executable);
            }
        }
        catch
        {
            Log("Rollback", "Apply failed. Rolling back from backup...");
            RestoreBackup(backupRoot, _config.InstallDir);
            throw;
        }

        await BackupManager.WriteBackupMetadataAsync(backupRoot, new BackupInfo
        {
            PreviousVersion = localManifest?.Version,
            NewVersion = remoteManifest.Version,
            AddedPaths = addedPaths
        }, _config.InstalledManifestPath, _config.InstallStatePath, cancellationToken);

        return backupRoot;
    }

    private async Task ProcessPackagesAsync(CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(_config.StagingDir, "packages");
        Directory.CreateDirectory(packageRoot);
        for (var index = 0; index < _config.Packages.Count; index++)
        {
            var package = _config.Packages[index];
            var archivePath = Path.Combine(packageRoot, package.Id + ".pkg");
            var file = new ManifestFile { Path = package.Id, Url = package.Url, Sha256 = package.Sha256, Size = package.Size };
            Log("Package", $"Downloading package {package.Id}", 84 + index);
            await DownloadWithRetryAsync(new Uri(package.Url), archivePath, file, cancellationToken);
            var destination = SafePath.ResolveInside(_config.InstallDir, package.ExtractTo);
            await PackageExtractor.ExtractAsync(archivePath, destination, package.Format, message => Log("Package", message), cancellationToken);
        }
    }

    private string GetEntryPointPath(LauncherManifest manifest) => SafePath.ResolveInside(_config.InstallDir, manifest.EntryPoint);

    private static void BackupFile(string source, string backupPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(source, backupPath, overwrite: true);
    }

    private static void RestoreBackup(string backupRoot, string installDir)
    {
        if (!Directory.Exists(backupRoot)) return;
        foreach (var backupFile in Directory.EnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backupRoot, backupFile);
            var target = SafePath.ResolveInside(installDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backupFile, target, overwrite: true);
        }
    }

    private static void TryMarkExecutable(string path, bool executable)
    {
        if (!executable || OperatingSystem.IsWindows()) return;
        TrySetUnixExecutable(path);
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void TrySetUnixExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set executable bit for {path}: {ex.Message}");
        }
    }

    private void Launch(LauncherManifest manifest)
    {
        var entryPoint = GetEntryPointPath(manifest);
        if (!File.Exists(entryPoint)) throw new FileNotFoundException("Entry point was not found after update.", entryPoint);
        var startInfo = new ProcessStartInfo
        {
            FileName = entryPoint,
            WorkingDirectory = Path.GetDirectoryName(entryPoint) ?? _config.InstallDir,
            UseShellExecute = false
        };
        foreach (var arg in _config.LaunchArguments ?? Array.Empty<string>()) startInfo.ArgumentList.Add(arg);
        var process = Process.Start(startInfo);
        if (process is not null) WriteAppPidFile(process.Id, entryPoint);
    }

    private void WriteAppPidFile(int pid, string entryPoint)
    {
        try
        {
            JsonFiles.WriteAsync(_config.AppPidPath, new AppPidInfo { Pid = pid, EntryPoint = entryPoint }).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Launch", $"Could not write app pid file ({_config.AppPidPath}): {ex.Message}");
        }
    }

    internal static void ValidateManifest(LauncherManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint)) throw new InvalidOperationException("Manifest entryPoint is required.");
        if (manifest.Files.Count == 0) throw new InvalidOperationException("Manifest files list is empty.");

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            _ = SafePath.ResolveInside("validation-root", file.Path);
            if (!seenPaths.Add(file.Path)) throw new InvalidOperationException($"Duplicate file path in manifest: {file.Path}");
            if (file.Size < 0) throw new InvalidOperationException($"Negative file size in manifest: {file.Path}");
            if (string.IsNullOrWhiteSpace(file.Sha256)) throw new InvalidOperationException($"Missing sha256 for {file.Path}");
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidOperationException($"Invalid sha256 format for {file.Path}");
        }

        if (!manifest.Files.Any(file => string.Equals(file.Path, manifest.EntryPoint, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Manifest entryPoint is not listed in files: {manifest.EntryPoint}");
        }
    }
}
