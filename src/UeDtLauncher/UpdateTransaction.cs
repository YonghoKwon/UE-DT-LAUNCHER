namespace UeDtLauncher;

public enum UpdateTransactionStatus
{
    Prepared,
    Applying,
    Committed,
    RolledBack
}

public sealed class UpdateTransactionJournal
{
    public string TransactionId { get; set; } = Guid.NewGuid().ToString("N");
    public UpdateTransactionStatus Status { get; set; }
    public string BackupName { get; set; } = string.Empty;
    public string? PreviousVersion { get; set; }
    public string NewVersion { get; set; } = string.Empty;
    public List<string> AddedPaths { get; set; } = new();
    public string CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}

internal sealed class UpdateTransactionContext
{
    private readonly LauncherConfig _config;

    internal UpdateTransactionContext(LauncherConfig config, UpdateTransactionJournal journal)
    {
        _config = config;
        Journal = journal;
    }

    internal UpdateTransactionJournal Journal { get; }

    internal string BackupRoot => UpdateTransactionManager.ResolveBackupRoot(_config, Journal);

    internal Task MarkApplyingAsync(CancellationToken cancellationToken) =>
        UpdateTransactionManager.SetStatusAsync(_config, Journal, UpdateTransactionStatus.Applying, cancellationToken);

    internal Task CommitAsync(CancellationToken cancellationToken) =>
        UpdateTransactionManager.CommitAsync(_config, Journal, cancellationToken);

    internal Task RollbackAsync(Action<string>? log = null, CancellationToken cancellationToken = default) =>
        UpdateTransactionManager.RollbackAsync(_config, Journal, log, cancellationToken);
}

internal static class UpdateTransactionManager
{
    internal static string JournalPath(LauncherConfig config) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(config.InstallStatePath))
            ?? throw new InvalidOperationException("Could not resolve the project state directory."),
            "transaction.json");

    internal static async Task<UpdateTransactionContext> BeginAsync(
        LauncherConfig config,
        string backupRoot,
        string? previousVersion,
        string newVersion,
        IEnumerable<string> addedPaths,
        CancellationToken cancellationToken)
    {
        var journal = new UpdateTransactionJournal
        {
            Status = UpdateTransactionStatus.Prepared,
            BackupName = Path.GetFileName(backupRoot),
            PreviousVersion = previousVersion,
            NewVersion = newVersion,
            AddedPaths = addedPaths.Distinct(PathComparer).ToList()
        };
        await JsonFiles.WriteAsync(JournalPath(config), journal, cancellationToken);
        return new UpdateTransactionContext(config, journal);
    }

    internal static async Task RecoverIfNeededAsync(
        LauncherConfig config,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var journalPath = JournalPath(config);
        if (!File.Exists(journalPath))
        {
            return;
        }

        var journal = await JsonFiles.ReadAsync<UpdateTransactionJournal>(journalPath, cancellationToken);
        switch (journal.Status)
        {
            case UpdateTransactionStatus.Prepared:
                DeletePreparedBackup(config, journal, log);
                DeleteJournal(journalPath);
                log?.Invoke($"Discarded incomplete prepared transaction {journal.TransactionId}.");
                break;
            case UpdateTransactionStatus.Applying:
                log?.Invoke($"Recovering interrupted update transaction {journal.TransactionId}.");
                await RollbackAsync(config, journal, log, cancellationToken);
                break;
            case UpdateTransactionStatus.Committed:
            case UpdateTransactionStatus.RolledBack:
                DeleteJournal(journalPath);
                break;
            default:
                throw new InvalidOperationException($"Unknown update transaction state: {journal.Status}");
        }
    }

    internal static async Task SetStatusAsync(
        LauncherConfig config,
        UpdateTransactionJournal journal,
        UpdateTransactionStatus status,
        CancellationToken cancellationToken)
    {
        journal.Status = status;
        await JsonFiles.WriteAsync(JournalPath(config), journal, cancellationToken);
    }

    internal static async Task CommitAsync(
        LauncherConfig config,
        UpdateTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        await SetStatusAsync(config, journal, UpdateTransactionStatus.Committed, cancellationToken);
        DeleteJournal(JournalPath(config));
    }

    internal static async Task RollbackAsync(
        LauncherConfig config,
        UpdateTransactionJournal journal,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (journal.Status == UpdateTransactionStatus.Prepared)
        {
            DeletePreparedBackup(config, journal, log);
            DeleteJournal(JournalPath(config));
            return;
        }

        var backupRoot = ResolveBackupRoot(config, journal);
        await BackupManager.RestoreAsync(
            backupRoot,
            config.InstallDir,
            config.InstalledManifestPath,
            config.InstallStatePath,
            log,
            cancellationToken);
        await SetStatusAsync(config, journal, UpdateTransactionStatus.RolledBack, cancellationToken);
        DeleteJournal(JournalPath(config));
    }

    internal static string ResolveBackupRoot(LauncherConfig config, UpdateTransactionJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.BackupName)
            || journal.BackupName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("Transaction backup name is invalid.");
        }

        return SafePath.ResolveInside(config.BackupDir, journal.BackupName);
    }

    private static void DeletePreparedBackup(
        LauncherConfig config,
        UpdateTransactionJournal journal,
        Action<string>? log)
    {
        var backupRoot = ResolveBackupRoot(config, journal);
        if (!Directory.Exists(backupRoot))
        {
            return;
        }

        Directory.Delete(backupRoot, recursive: true);
        log?.Invoke($"Removed incomplete prepared backup {journal.BackupName}.");
    }

    private static void DeleteJournal(string journalPath)
    {
        if (File.Exists(journalPath)) File.Delete(journalPath);
    }

    private static StringComparer PathComparer => SafePath.FileSystemComparer;
}
