using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;

namespace UeDtLauncher;

public sealed class LauncherEngine : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Action<LauncherProgress>? _progress;
    private readonly FileLogger? _fileLogger;
    private readonly bool _echoToConsole;
    private readonly bool _ownsHttpClient;

    public LauncherEngine(LauncherConfig config, Action<LauncherProgress>? progress = null, FileLogger? fileLogger = null, bool echoToConsole = true)
        : this(config, progress, fileLogger, echoToConsole, httpClient: null)
    {
    }

    internal LauncherEngine(
        LauncherConfig config,
        Action<LauncherProgress>? progress,
        FileLogger? fileLogger,
        bool echoToConsole,
        HttpClient? httpClient)
    {
        _config = config;
        _progress = progress;
        _fileLogger = fileLogger;
        _echoToConsole = echoToConsole;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? SecureHttpClientFactory.Create(config);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds));
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var prepared = await PrepareAsync(cancellationToken);
        _ = await CommitPreparedAsync(prepared, cancellationToken);
        await CompleteRunAsync(prepared.RemoteManifest, cancellationToken);
    }

    internal async Task<PreparedLauncherUpdate> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var instanceLock = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(_config));
        try
        {
            Directory.CreateDirectory(_config.InstallDir);
            Directory.CreateDirectory(_config.StagingDir);
            Directory.CreateDirectory(_config.BackupDir);
            await UpdateTransactionManager.RecoverIfNeededAsync(
                _config,
                message => Log("Recovery", message),
                cancellationToken);

            Log("Manifest", "Downloading remote manifest...", 5);
            var manifestDocument = await ManifestDownloader.DownloadAsync(
                _config,
                _httpClient,
                (stage, message, percent) => Log(stage, message, percent),
                cancellationToken);
            var remoteManifest = manifestDocument.Manifest;
            var manifestJson = manifestDocument.Json;

            Log("Manifest", $"App: {remoteManifest.AppId} / Version: {remoteManifest.Version} / Platform: {remoteManifest.Platform}", 10);
            var localManifest = await TryLoadLocalManifestAsync(cancellationToken);
            var localState = await TryLoadInstallStateAsync(cancellationToken);

            Log("Plan", "Building update plan...", 15);
            var plan = await BuildPlanAsync(remoteManifest, localManifest, cancellationToken);
            var packagesToPrepare = _config.Packages
                .Where(package => ShouldPreparePackage(package, localState, _config.RepairMode))
                .ToList();
            Log("Plan", $"Download/repair: {plan.DownloadOrRepair.Count}, Remove: {plan.Remove.Count}, Packages: {packagesToPrepare.Count}", 20);

            var preparedPackages = new PreparedPackages();
            if (plan.HasChanges || packagesToPrepare.Count > 0)
            {
                var requiredBytes = plan.DownloadOrRepair.Sum(file => Math.Max(0, file.Size))
                                    + packagesToPrepare.Sum(package => Math.Max(0, package.Size));
                DiskSpace.EnsureAvailable(_config.StagingDir, requiredBytes, (stage, message) => Log(stage, message));
                DiskSpace.EnsureAvailable(_config.InstallDir, requiredBytes, (stage, message) => Log(stage, message));

                Log("Download", "Downloading changed files to staging...", 35);
                await PrepareStagingAsync(plan, remoteManifest, cancellationToken);
                preparedPackages = await PreparePackagesAsync(packagesToPrepare, remoteManifest, cancellationToken);
            }

            return new PreparedLauncherUpdate(
                remoteManifest,
                manifestJson,
                localManifest,
                localState,
                plan,
                preparedPackages,
                MergePackageHashes(localState, preparedPackages),
                packagesToPrepare.Count > 0,
                instanceLock);
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    internal async Task<string?> CommitPreparedAsync(
        PreparedLauncherUpdate prepared,
        CancellationToken cancellationToken = default)
    {
        prepared.ThrowIfDisposed();
        if (!prepared.HasLiveChanges)
        {
            Log("Plan", "Already up to date.", 70);
            if (!File.Exists(_config.InstallStatePath) || prepared.PackagePreparationRequested)
            {
                await WriteInstallStateAsync(
                    prepared.RemoteManifest,
                    prepared.ManifestJson,
                    prepared.LocalState?.LastBackupRoot,
                    prepared.PackageHashes,
                    prepared.Packages.SkippedOptionalPackages,
                    cancellationToken);
            }
            return null;
        }

        Log("Apply", "Applying update with backup...", 70);
        var transaction = await ApplyUpdateAsync(
            prepared.Plan,
            prepared.Packages.Files,
            prepared.LocalManifest,
            prepared.RemoteManifest,
            cancellationToken);
        try
        {
            Log("Manifest", "Writing installed manifest...", 80);
            await JsonFiles.WriteAsync(_config.InstalledManifestPath, prepared.RemoteManifest, cancellationToken);
            await WriteInstallStateAsync(
                prepared.RemoteManifest,
                prepared.ManifestJson,
                transaction.BackupRoot,
                prepared.PackageHashes,
                prepared.Packages.SkippedOptionalPackages,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            Log("Rollback", "State commit failed. Rolling back the update...");
            await transaction.RollbackAsync(message => Log("Rollback", message), CancellationToken.None);
            throw;
        }
        BackupManager.Prune(_config.BackupDir, _config.MaxBackupCount, message => Log("Backup", message));
        return transaction.BackupRoot;
    }

    internal void LaunchPrepared(PreparedLauncherUpdate prepared)
    {
        prepared.ThrowIfDisposed();
        Launch(prepared.RemoteManifest);
    }

    internal void LaunchPrevious(PreparedLauncherUpdate prepared)
    {
        prepared.ThrowIfDisposed();
        Launch(prepared.LocalManifest ?? prepared.RemoteManifest);
    }

    private async Task CompleteRunAsync(LauncherManifest remoteManifest, CancellationToken cancellationToken)
    {
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
        if (_echoToConsole)
        {
            Console.WriteLine(percent.HasValue ? $"[{stage}] {message} ({percent:0}%)" : $"[{stage}] {message}");
        }
        _fileLogger?.Log(stage, message);
        _progress?.Invoke(new LauncherProgress(stage, message, percent));
    }

    private async Task WriteInstallStateAsync(
        LauncherManifest manifest,
        string manifestJson,
        string? backupRoot,
        IReadOnlyDictionary<string, string> appliedPackageHashes,
        IReadOnlyCollection<string> skippedOptionalPackages,
        CancellationToken cancellationToken)
    {
        var state = new InstallState
        {
            Version = manifest.Version,
            Channel = manifest.Channel,
            Environment = _config.Environment,
            Platform = manifest.Platform,
            ManifestSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant(),
            LastBackupRoot = backupRoot,
            AppliedPackageHashes = new Dictionary<string, string>(appliedPackageHashes, StringComparer.OrdinalIgnoreCase),
            SkippedOptionalPackages = skippedOptionalPackages.ToList()
        };
        await JsonFiles.WriteAsync(_config.InstallStatePath, state, cancellationToken);
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

    private async Task<InstallState?> TryLoadInstallStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_config.InstallStatePath)) return null;
        try
        {
            return await JsonFiles.ReadAsync<InstallState>(_config.InstallStatePath, cancellationToken);
        }
        catch (Exception ex)
        {
            Log("Repair", $"Install state is unreadable and will be rebuilt. Reason: {ex.Message}");
            return null;
        }
    }

    internal static bool ShouldPreparePackage(
        LauncherPackage package,
        InstallState? state,
        bool repairMode)
    {
        if (repairMode) return true;
        return state is null
               || !state.AppliedPackageHashes.TryGetValue(package.Id, out var installedHash)
               || !string.Equals(installedHash, package.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> MergePackageHashes(
        InstallState? localState,
        PreparedPackages preparedPackages)
    {
        var result = localState is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(localState.AppliedPackageHashes, StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, hash) in preparedPackages.AppliedPackageHashes)
        {
            result[packageId] = hash;
        }

        return result;
    }

    private async Task<UpdatePlan> BuildPlanAsync(LauncherManifest remote, LauncherManifest? local, CancellationToken cancellationToken)
    {
        var plan = new UpdatePlan();
        var localByPath = local?.Files.ToDictionary(file => file.Path, SafePath.FileSystemComparer)
                          ?? new Dictionary<string, ManifestFile>(SafePath.FileSystemComparer);

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
            var remotePaths = remote.Files.Select(file => file.Path).ToHashSet(SafePath.FileSystemComparer);
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

        var fileCount = plan.DownloadOrRepair.Count;
        var totalBytes = plan.DownloadOrRepair.Sum(file => Math.Max(0, file.Size));
        long completedBytes = 0;

        for (var index = 0; index < fileCount; index++)
        {
            var file = plan.DownloadOrRepair[index];
            cancellationToken.ThrowIfCancellationRequested();
            var stagingPath = SafePath.ResolveInside(_config.StagingDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            var downloadUri = ResolveDownloadUri(remote, file);
            Log("Download", $"({index + 1}/{fileCount}) {file.Path}", DownloadPercent(completedBytes, totalBytes));

            var fileBaseBytes = completedBytes;
            var fileIndex = index + 1;
            void OnBytes(long bytesForFile) => _progress?.Invoke(new LauncherProgress(
                "DownloadProgress", file.Path,
                DownloadPercent(fileBaseBytes + bytesForFile, totalBytes),
                fileBaseBytes + bytesForFile, totalBytes, fileIndex, fileCount));

            await DownloadWithRetryAsync(downloadUri, stagingPath, file, OnBytes, cancellationToken);
            completedBytes += Math.Max(0, file.Size);
        }
    }

    // Downloads occupy the 35..65 band of the overall progress bar.
    private static double DownloadPercent(long bytesDone, long totalBytes) =>
        totalBytes <= 0 ? 35 : 35 + Math.Clamp(bytesDone / (double)totalBytes, 0, 1) * 30;

    private Uri ResolveDownloadUri(LauncherManifest manifest, ManifestFile file)
    {
        if (Uri.TryCreate(file.Url, UriKind.Absolute, out var absolute))
        {
            LauncherConfigValidator.ValidateUrl(_config, absolute, "file download");
            return absolute;
        }
        var baseUrl = manifest.BaseUrl ?? throw new InvalidOperationException($"File URL is relative but manifest.baseUrl is missing: {file.Path}");
        var resolved = new Uri(baseUrl.TrimEnd('/') + "/" + (file.Url ?? file.Path).TrimStart('/'));
        LauncherConfigValidator.ValidateUrl(_config, resolved, "file download");
        return resolved;
    }

    private async Task DownloadWithRetryAsync(Uri uri, string targetPath, ManifestFile file, Action<long>? onBytes, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var maxAttempts = Math.Max(1, _config.MaxRetryCount);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await DownloadFileAsync(uri, targetPath, file.Size, onBytes, cancellationToken);
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

    private async Task DownloadFileAsync(Uri uri, string targetPath, long expectedSize, Action<long>? onBytes, CancellationToken cancellationToken)
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
        if (existingLength > 0)
        {
            var rangeError = ValidateResumeResponse(response, existingLength, expectedSize);
            if (rangeError is not null)
            {
                await DeleteFileWithRetryAsync(tempPath, cancellationToken);
                throw new IOException(rangeError);
            }
        }

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = new FileStream(tempPath, existingLength > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            var written = existingLength;
            var lastReport = Environment.TickCount64;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (onBytes is not null && Environment.TickCount64 - lastReport >= 200)
                {
                    onBytes(written);
                    lastReport = Environment.TickCount64;
                }
            }

            await target.FlushAsync(cancellationToken);
            onBytes?.Invoke(written);
        }

        var actualSize = new FileInfo(tempPath).Length;
        if (expectedSize > 0 && actualSize != expectedSize) throw new IOException($"Size mismatch. Expected {expectedSize}, actual {actualSize}.");
        await MoveFileWithRetryAsync(tempPath, targetPath, cancellationToken);
    }

    internal static string? ValidateResumeResponse(HttpResponseMessage response, long existingLength, long expectedSize)
    {
        if (existingLength <= 0) return null;
        if (response.StatusCode != HttpStatusCode.PartialContent) return "HTTP server did not honor the resume Range request.";
        var range = response.Content.Headers.ContentRange;
        if (range?.From != existingLength) return "HTTP Content-Range start did not match the requested resume offset.";
        if (expectedSize > 0 && range.Length != expectedSize) return "HTTP Content-Range total did not match the manifest size.";
        return null;
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

    private async Task<UpdateTransactionContext> ApplyUpdateAsync(
        UpdatePlan plan,
        IReadOnlyCollection<PreparedPackageFile> packageFiles,
        LauncherManifest? localManifest,
        LauncherManifest remoteManifest,
        CancellationToken cancellationToken)
    {
        var backupRoot = BackupManager.CreateBackupRoot(_config.BackupDir);
        var addedPaths = plan.DownloadOrRepair
            .Where(file => !File.Exists(SafePath.ResolveInsideChecked(_config.InstallDir, file.Path)))
            .Select(file => file.Path)
            .Concat(packageFiles
                .Where(file => !File.Exists(SafePath.ResolveInsideChecked(_config.InstallDir, file.RelativeInstallPath)))
                .Select(file => file.RelativeInstallPath))
            .ToList();
        var transaction = await UpdateTransactionManager.BeginAsync(
            _config,
            backupRoot,
            localManifest?.Version,
            remoteManifest.Version,
            addedPaths,
            cancellationToken);

        try
        {
            // Capture all pre-update data before marking the transaction as Applying.
            // If backup preparation is interrupted, recovery can discard it without touching live files.
            await BackupManager.WriteBackupMetadataAsync(backupRoot, new BackupInfo
            {
                PreviousVersion = localManifest?.Version,
                NewVersion = remoteManifest.Version,
                AddedPaths = addedPaths
            }, _config.InstalledManifestPath, _config.InstallStatePath, cancellationToken);

            foreach (var relativePath in plan.Remove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, relativePath);
                if (File.Exists(installedPath)) BackupFile(installedPath, SafePath.ResolveInside(backupRoot, relativePath));
            }

            foreach (var file in plan.DownloadOrRepair)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, file.Path);
                var backupPath = SafePath.ResolveInside(backupRoot, file.Path);
                if (File.Exists(installedPath)) BackupFile(installedPath, backupPath);
            }

            foreach (var file in packageFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, file.RelativeInstallPath);
                var backupPath = SafePath.ResolveInside(backupRoot, file.RelativeInstallPath);
                if (File.Exists(installedPath)) BackupFile(installedPath, backupPath);
            }

            await transaction.MarkApplyingAsync(cancellationToken);

            foreach (var relativePath in plan.Remove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, relativePath);
                if (File.Exists(installedPath)) File.Delete(installedPath);
            }

            foreach (var file in plan.DownloadOrRepair)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = SafePath.ResolveInside(_config.StagingDir, file.Path);
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
                File.Move(stagingPath, installedPath, overwrite: true);
                TryMarkExecutable(installedPath, file.Executable);
            }

            foreach (var file in packageFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installedPath = SafePath.ResolveInsideChecked(_config.InstallDir, file.RelativeInstallPath);
                Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
                File.Move(file.SourcePath, installedPath, overwrite: true);
            }
        }
        catch
        {
            Log("Rollback", "Apply failed. Rolling back the transaction...");
            await transaction.RollbackAsync(message => Log("Rollback", message), CancellationToken.None);
            throw;
        }

        return transaction;
    }

    private async Task<PreparedPackages> PreparePackagesAsync(
        IReadOnlyList<LauncherPackage> packages,
        LauncherManifest remoteManifest,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedPackages();
        if (packages.Count == 0) return prepared;

        var packageRoot = Path.Combine(_config.StagingDir, "packages");
        var expandedRoot = Path.Combine(_config.StagingDir, "package-expanded");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(expandedRoot);

        var pathComparer = SafePath.FileSystemComparer;
        var manifestPaths = remoteManifest.Files
            .Select(file => CanonicalRelativePath(file.Path))
            .ToHashSet(pathComparer);
        var packagePaths = new HashSet<string>(pathComparer);
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            try
            {
                ValidatePackage(package, packageIds);
                var archivePath = SafePath.ResolveInside(packageRoot, package.Id + ".pkg");
                var file = new ManifestFile
                {
                    Path = package.Id,
                    Url = package.Url,
                    Sha256 = package.Sha256,
                    Size = package.Size
                };
                Log("Package", $"Downloading package {package.Id}", 65 + index);
                await DownloadWithRetryAsync(new Uri(package.Url), archivePath, file, onBytes: null, cancellationToken);

                var expandedDir = SafePath.ResolveInside(expandedRoot, package.Id);
                Directory.CreateDirectory(expandedDir);
                await PackageExtractor.ExtractAsync(
                    archivePath,
                    expandedDir,
                    package.Format,
                    message => Log("Package", message),
                    cancellationToken);

                prepared.Files.AddRange(BuildPreparedPackageFiles(
                    package,
                    expandedDir,
                    manifestPaths,
                    packagePaths));
                prepared.AppliedPackageHashes[package.Id] = package.Sha256;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!package.Required)
            {
                prepared.SkippedOptionalPackages.Add(package.Id);
                Log("Package", $"Optional package {package.Id} was skipped: {ex.GetBaseException().Message}");
            }
        }

        return prepared;
    }

    internal static IReadOnlyList<PreparedPackageFile> BuildPreparedPackageFiles(
        LauncherPackage package,
        string expandedDir,
        IReadOnlySet<string> manifestPaths,
        ISet<string> packagePaths)
    {
        var files = new List<PreparedPackageFile>();
        var localPaths = new HashSet<string>(
            packagePaths,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var sourcePath in Directory.EnumerateFiles(expandedDir, "*", SearchOption.AllDirectories))
        {
            var extractedRelative = Path.GetRelativePath(expandedDir, sourcePath);
            var combined = Path.Combine(package.ExtractTo, extractedRelative);
            var installRelative = CanonicalRelativePath(combined);
            if (manifestPaths.Contains(installRelative))
            {
                throw new InvalidOperationException(
                    $"Package {package.Id} would overwrite manifest-managed file: {installRelative}");
            }

            if (!localPaths.Add(installRelative))
            {
                throw new InvalidOperationException(
                    $"Multiple packages target the same install path: {installRelative}");
            }

            files.Add(new PreparedPackageFile(package.Id, sourcePath, installRelative));
        }

        foreach (var file in files) packagePaths.Add(file.RelativeInstallPath);
        return files;
    }

    private static void ValidatePackage(LauncherPackage package, ISet<string> packageIds)
    {
        _ = SafePath.ResolveInside("validation-root", package.Id + ".pkg");
        _ = SafePath.ResolveInside("validation-root", package.ExtractTo);
        if (!packageIds.Add(package.Id)) throw new InvalidOperationException($"Duplicate package id: {package.Id}");
        if (package.Size < 0) throw new InvalidOperationException($"Negative package size: {package.Id}");
        if (package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException($"Invalid package sha256: {package.Id}");
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Package URL must be absolute: {package.Id}");
    }

    private static string CanonicalRelativePath(string relativePath)
    {
        var validationRoot = Path.GetFullPath("validation-root");
        var fullPath = SafePath.ResolveInside(validationRoot, relativePath);
        return Path.GetRelativePath(validationRoot, fullPath);
    }

    private string GetEntryPointPath(LauncherManifest manifest) => SafePath.ResolveInside(_config.InstallDir, manifest.EntryPoint);

    private static void BackupFile(string source, string backupPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(source, backupPath, overwrite: true);
    }

    private static void TryMarkExecutable(string path, bool executable)
    {
        if (!executable || OperatingSystem.IsWindows()) return;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            TrySetUnixExecutable(path);
        }
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

        var seenPaths = new HashSet<string>(SafePath.FileSystemComparer);
        foreach (var file in manifest.Files)
        {
            var canonicalPath = CanonicalRelativePath(file.Path);
            if (!seenPaths.Add(canonicalPath)) throw new InvalidOperationException($"Duplicate file path in manifest: {file.Path}");
            if (file.Size < 0) throw new InvalidOperationException($"Negative file size in manifest: {file.Path}");
            if (string.IsNullOrWhiteSpace(file.Sha256)) throw new InvalidOperationException($"Missing sha256 for {file.Path}");
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidOperationException($"Invalid sha256 format for {file.Path}");
        }

        if (!manifest.Files.Any(file => string.Equals(file.Path, manifest.EntryPoint, SafePath.FileSystemComparison)))
        {
            throw new InvalidOperationException($"Manifest entryPoint is not listed in files: {manifest.EntryPoint}");
        }
    }

    internal static void ValidateManifest(LauncherManifest manifest, LauncherConfig config)
    {
        ValidateManifest(manifest);
        if (config.SchemaVersion < 2) return;
        if (!string.IsNullOrWhiteSpace(config.ProjectId) && !manifest.AppId.Equals(config.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest appId '{manifest.AppId}' does not match selected project '{config.ProjectId}'.");
        if (!manifest.Platform.Equals(config.TargetPlatform, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest platform '{manifest.Platform}' does not match selected platform '{config.TargetPlatform}'.");
        if (!string.IsNullOrWhiteSpace(config.ResolvedReleaseVersion) && !manifest.Version.Equals(config.ResolvedReleaseVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest version '{manifest.Version}' does not match catalog release '{config.ResolvedReleaseVersion}'.");
        if (!manifest.Channel.Equals(config.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Manifest channel '{manifest.Channel}' does not match selected channel '{config.Channel}'.");
        if (manifest.Files.Count > config.Security.MaxManifestFiles)
            throw new InvalidDataException("Manifest file count exceeds the configured limit.");
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file.Size > config.Security.MaxSingleFileBytes) throw new InvalidDataException($"Manifest file exceeds the configured size limit: {file.Path}");
            total = checked(total + file.Size);
            if (total > config.Security.MaxTotalDownloadBytes) throw new InvalidDataException("Manifest total download size exceeds the configured limit.");
        }
    }
}
