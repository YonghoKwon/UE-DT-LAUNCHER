using Avalonia;
using UeDtLauncher.Gui;

namespace UeDtLauncher;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (SelfUpdateManager.TryApplyPendingUpdate(args)) return 0;

        if (args.Length == 0 || string.Equals(args[0], "gui", StringComparison.OrdinalIgnoreCase))
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args.Length == 0 ? Array.Empty<string>() : args.Skip(1).ToArray());
        }

        return MainAsync(args).GetAwaiter().GetResult();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        try
        {
            if (Has(args, "--help") || Has(args, "-h"))
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            return command switch
            {
                "run" => await RunLauncherAsync(args.Skip(1).ToArray()),
                "service" => await RunServiceAsync(args.Skip(1).ToArray()),
                "rollback" => await RollbackAsync(args.Skip(1).ToArray()),
                "generate-manifest" => await GenerateManifestAsync(args.Skip(1).ToArray()),
                "update-catalog" => await UpdateCatalogAsync(args.Skip(1).ToArray()),
                "sample-config" => await WriteSampleConfigAsync(args.Skip(1).ToArray()),
                "sign-manifest" => await SignManifestAsync(args.Skip(1).ToArray()),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunLauncherAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var repair = Has(args, "--repair");
        var noLaunch = Has(args, "--no-launch");

        var config = await JsonFiles.ReadAsync<LauncherConfig>(configPath);
        if (repair) config.RepairMode = true;
        if (noLaunch) config.LaunchAfterUpdate = false;

        var fileLogger = new FileLogger(config.LogDir);
        await ResolveCatalogForCliAsync(config);
        using var engine = new LauncherEngine(config, progress: null, fileLogger);
        await engine.RunAsync();
        return 0;
    }

    private static async Task<int> RunServiceAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var once = Has(args, "--once");
        int? interval = null;
        var intervalArg = Get(args, "--interval");
        if (!string.IsNullOrWhiteSpace(intervalArg))
        {
            if (!int.TryParse(intervalArg, out var parsed) || parsed <= 0) throw new ArgumentException("--interval must be a positive number of seconds.");
            interval = parsed;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // shut the loop down cleanly instead of killing the process
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // systemd stop sends SIGTERM; the source may already be disposed on normal exit.
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        };

        return await ServiceRunner.RunAsync(configPath, interval, once, cts.Token);
    }

    private static async Task<int> RollbackAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var config = await JsonFiles.ReadAsync<LauncherConfig>(configPath);
        var backups = BackupManager.List(config.BackupDir);

        if (Has(args, "--list"))
        {
            if (backups.Count == 0)
            {
                Console.WriteLine("No backups found in: " + config.BackupDir);
                return 0;
            }

            Console.WriteLine("Available backups (newest first):");
            foreach (var (backupRoot, info) in backups)
            {
                var name = Path.GetFileName(backupRoot);
                Console.WriteLine($"  {name}  previous: {info?.PreviousVersion ?? "?"}  updated to: {info?.NewVersion ?? "?"}  created: {info?.CreatedAtUtc ?? "?"}");
            }

            return 0;
        }

        var requested = Get(args, "--backup");
        var selected = string.IsNullOrWhiteSpace(requested)
            ? backups.FirstOrDefault()
            : backups.FirstOrDefault(backup => string.Equals(Path.GetFileName(backup.BackupRoot), requested, StringComparison.Ordinal));

        if (selected.BackupRoot is null)
        {
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(requested)
                ? "No backups found in: " + config.BackupDir
                : $"Backup not found: {requested}. Use 'rollback --list' to see available backups.");
            return 1;
        }

        var fileLogger = new FileLogger(config.LogDir);
        using var instanceLock = SingleInstanceLock.Acquire(SingleInstanceLock.LockPathFor(config.InstallDir));
        Console.WriteLine($"Rolling back using backup {Path.GetFileName(selected.BackupRoot)}...");
        await BackupManager.RestoreAsync(selected.BackupRoot, config.InstallDir, config.InstalledManifestPath, config.InstallStatePath, message =>
        {
            Console.WriteLine("[Rollback] " + message);
            fileLogger.Log("Rollback", message);
        });
        Console.WriteLine("Rollback completed.");
        return 0;
    }

    private static async Task ResolveCatalogForCliAsync(LauncherConfig config)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, config.HttpTimeoutSeconds)) };
        await CatalogResolver.ResolveAsync(config, httpClient, (stage, message, percent) =>
        {
            Console.WriteLine(percent.HasValue ? $"[{stage}] {message} ({percent:0}%)" : $"[{stage}] {message}");
        });
    }

    private static async Task<int> GenerateManifestAsync(string[] args)
    {
        var packageDir = Required(args, "--package-dir");
        var output = Get(args, "--output") ?? Path.Combine(packageDir, "manifest.json");
        var baseUrl = Required(args, "--base-url");
        var entryPoint = Required(args, "--entry-point");
        var version = Get(args, "--version") ?? "1.0.0";
        var channel = Get(args, "--channel") ?? "stable";
        var platform = Get(args, "--platform") ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");
        var appId = Get(args, "--app-id");

        await ManifestGenerator.GenerateAsync(packageDir, output, baseUrl, entryPoint, version, channel, platform, appId);
        return 0;
    }

    private static async Task<int> UpdateCatalogAsync(string[] args)
    {
        var catalogPath = Required(args, "--catalog");
        var projectId = Required(args, "--project-id");
        var version = Required(args, "--version");
        var environment = Get(args, "--environment") ?? "prod";
        var channel = Get(args, "--channel") ?? "stable";
        var platform = Get(args, "--platform") ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");

        if (Has(args, "--remove"))
        {
            await CatalogUpdater.RemoveReleaseAsync(catalogPath, projectId, version, environment, channel, platform, Has(args, "--remove-project-if-empty"));
            Console.WriteLine($"Release removed (if present): {projectId} {version} {environment}/{channel}/{platform}");
            Console.WriteLine($"Catalog updated: {catalogPath}");
            return 0;
        }

        var profiles = (Get(args, "--allowed-profiles") ?? "general")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        await CatalogUpdater.UpsertReleaseAsync(catalogPath, new CatalogReleaseUpdate
        {
            ProjectId = projectId,
            DisplayName = Get(args, "--display-name") ?? projectId,
            Version = version,
            Environment = environment,
            Channel = channel,
            Platform = platform,
            ManifestUrl = Required(args, "--manifest-url"),
            ManifestSignatureUrl = Get(args, "--manifest-signature-url"),
            AllowedClientProfiles = profiles,
            Notes = Get(args, "--notes"),
            SetLatest = Has(args, "--set-latest")
        });

        Console.WriteLine($"Release upserted: {projectId} {version} {environment}/{channel}/{platform} (profiles: {string.Join(",", profiles)})");
        Console.WriteLine($"Catalog updated: {catalogPath}");
        return 0;
    }

    private static async Task<int> SignManifestAsync(string[] args)
    {
        var manifestPath = Required(args, "--manifest");
        var privateKeyPath = Required(args, "--private-key");
        var output = Get(args, "--output") ?? manifestPath + ".sig";
        var payload = await File.ReadAllTextAsync(manifestPath);
        var privateKey = await File.ReadAllTextAsync(privateKeyPath);
        await File.WriteAllTextAsync(output, ManifestSignatureVerifier.Sign(payload, privateKey));
        Console.WriteLine($"Manifest signature written: {output}");
        return 0;
    }

    private static async Task<int> WriteSampleConfigAsync(string[] args)
    {
        var output = Get(args, "--output") ?? "launcher.config.json";
        var config = new LauncherConfig
        {
            CatalogUrl = "https://updates.example.com/catalogs/general/catalog.json",
            CatalogSignatureUrl = "https://updates.example.com/catalogs/general/catalog.json.sig",
            CatalogPublicKeyPath = "manifest-public-key.pem",
            ProjectId = "ue-dt-simulator",
            ClientProfile = "general",
            Environment = "prod",
            Channel = "stable",
            VersionPolicy = "latest",
            TargetPlatform = "windows-x64",
            ManifestUrl = "https://your-update-server.example.com/windows-x64/manifest.json",
            ManifestSignatureUrl = "https://your-update-server.example.com/windows-x64/manifest.json.sig",
            ManifestPublicKeyPath = "manifest-public-key.pem",
            RequireSignedManifests = false,
            InstallDir = "app",
            StagingDir = ".staging",
            BackupDir = ".backup",
            InstalledManifestPath = "installed-manifest.json",
            InstallStatePath = "install-state.json",
            AppPidPath = "app.pid",
            LogDir = "logs",
            MaxBackupCount = 3,
            LaunchAfterUpdate = true,
            RepairMode = false,
            RemoveFilesNotInManifest = false,
            MaxRetryCount = 3,
            HttpTimeoutSeconds = 300,
            LaunchArguments = new[] { "-log" },
            ServiceMode = new ServiceModeConfig
            {
                IntervalSeconds = 300,
                AutoRestartApp = true,
                ProcessName = null
            },
            WindowsIntegration = new WindowsIntegrationConfig
            {
                CreateDesktopShortcut = false,
                CreateStartMenuShortcut = false,
                RegisterAppEntry = false
            }
        };
        await JsonFiles.WriteAsync(output, config);
        Console.WriteLine($"Sample config written: {output}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static string Required(string[] args, string name)
    {
        var value = Get(args, name);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Missing required argument: {name}");
        return value;
    }

    private static string? Get(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static bool Has(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static void PrintHelp()
    {
        Console.WriteLine("UE-DT-LAUNCHER");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  gui");
        Console.WriteLine("  sample-config --output launcher.config.json");
        Console.WriteLine("  generate-manifest --package-dir <dir> --base-url <url> --entry-point <relative path> --version <version> [--app-id <id>] --output <manifest.json>");
        Console.WriteLine("  update-catalog --catalog <catalog.json> --project-id <id> --version <version> --environment <prod|dev> --channel <stable|beta|dev> --platform <windows-x64|linux-x64> --manifest-url <url> [--display-name <name>] [--allowed-profiles general,developer] [--notes <text>] [--set-latest] [--remove] [--remove-project-if-empty]");
        Console.WriteLine("  sign-manifest --manifest <manifest.json> --private-key <private.pem> --output <manifest.json.sig>");
        Console.WriteLine("  run --config launcher.config.json [--repair] [--no-launch]");
        Console.WriteLine("  service --config launcher.config.json [--interval <seconds>] [--once]");
        Console.WriteLine("  rollback --config launcher.config.json [--list] [--backup <timestamp>]");
    }
}
