using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using UeDtLauncher.Gui;

namespace UeDtLauncher;

public static class Program
{
    private static readonly string[] KnownSubcommands =
    {
        "run", "service", "rollback", "generate-manifest", "update-catalog",
        "list-releases", "generate-nginx-acl", "sign-manifest", "generate-signing-key", "sample-config", "publish-release", "doctor", "diagnostics", "agent", "credential"
    };

    [STAThread]
    public static int Main(string[] args)
    {
        CrashReporter.Install(Path.Combine(AppContext.BaseDirectory, "logs"));
        if (args.Any(arg => arg.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString());
            return 0;
        }
        var wantsGui = Has(args, "--gui");
        var wantsCli = Has(args, "--cli");

        if (wantsGui && wantsCli)
        {
            Console.Error.WriteLine("--gui and --cli cannot be used together.");
            return 2;
        }

        // GUI when: --gui present, OR (no --cli AND (args empty OR args[0]=="gui")).
        var isGui = wantsGui
            || (!wantsCli && (args.Length == 0 || string.Equals(args[0], "gui", StringComparison.OrdinalIgnoreCase)));

        // The app is a GUI-subsystem (WinExe) build so double-clicking the launcher shows no console
        // window. For CLI subcommands launched from a terminal, attach to that terminal so output is visible.
        if (!isGui && OperatingSystem.IsWindows()) AttachParentConsole();

        if (SelfUpdateManager.TryApplyPendingUpdate(args)) return 0;

        if (isGui)
        {
            // Headless guard: no graphical display available on Linux.
            if (GuiUnavailable(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                Console.Error.WriteLine("No graphical display detected (DISPLAY/WAYLAND_DISPLAY are not set).");
                Console.Error.WriteLine("The GUI requires an X11 or Wayland desktop. On a headless server, run the launcher in CLI mode instead:");
                Console.Error.WriteLine("  UeDtLauncher --cli --config launcher.config.json");
                Console.Error.WriteLine("  UeDtLauncher run --config launcher.config.json");
                Console.Error.WriteLine("  UeDtLauncher service --config launcher.config.json");
                return 1;
            }

            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(GuiArgs(args));
            }
            catch (Exception ex) when (OperatingSystem.IsLinux())
            {
                Console.Error.WriteLine("Failed to start the graphical interface: " + ex.Message);
                Console.Error.WriteLine("A display was detected but GUI initialization failed (likely no usable display server or missing fonts).");
                Console.Error.WriteLine("Install CJK fonts (e.g. 'sudo dnf install -y google-noto-sans-cjk-fonts' or 'sudo apt install fonts-noto-cjk'), or run in CLI mode:");
                Console.Error.WriteLine("  UeDtLauncher --cli --config launcher.config.json");
                Console.Error.WriteLine("  UeDtLauncher run --config launcher.config.json");
                Console.Error.WriteLine("  UeDtLauncher service --config launcher.config.json");
                return 1;
            }
        }

        return MainAsync(CliArgs(args, wantsCli)).GetAwaiter().GetResult();
    }

    // Returns true when running on Linux with neither X11 (DISPLAY) nor Wayland (WAYLAND_DISPLAY) available.
    internal static bool GuiUnavailable(bool isLinux, string? display, string? wayland)
        => isLinux && string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(wayland);

    // Strip a leading "gui" token and any "--gui" token before handing args to Avalonia.
    internal static string[] GuiArgs(string[] args)
    {
        IEnumerable<string> rest = args;
        if (args.Length > 0 && string.Equals(args[0], "gui", StringComparison.OrdinalIgnoreCase))
        {
            rest = args.Skip(1);
        }

        return rest.Where(arg => !string.Equals(arg, "--gui", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // Map CLI invocation to a subcommand. When --cli is used, default to the "run" client update
    // unless the remaining args already start with a known subcommand.
    internal static string[] CliArgs(string[] args, bool wantsCli)
    {
        if (!wantsCli) return args;

        var rest = args.Where(arg => !string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rest.Length > 0 && KnownSubcommands.Contains(rest[0], StringComparer.OrdinalIgnoreCase))
        {
            return rest;
        }

        return new[] { "run" }.Concat(rest).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static void AttachParentConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { /* no parent console (e.g. double-clicked) — ignore */ }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

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
                "list-releases" => await ListReleasesAsync(args.Skip(1).ToArray()),
                "generate-nginx-acl" => await GenerateNginxAclAsync(args.Skip(1).ToArray()),
                "sample-config" => await WriteSampleConfigAsync(args.Skip(1).ToArray()),
                "sign-manifest" => await SignManifestAsync(args.Skip(1).ToArray()),
                "agent" => await RunAgentClientAsync(args.Skip(1).ToArray()),
                "credential" => RunCredentialCommand(args.Skip(1).ToArray()),
                "publish-release" => await PublishReleaseAsync(args.Skip(1).ToArray()),
                "generate-signing-key" => await GenerateSigningKeyAsync(args.Skip(1).ToArray()),
                "doctor" => await RunDoctorAsync(args.Skip(1).ToArray()),
                "diagnostics" => await RunDiagnosticsAsync(args.Skip(1).ToArray()),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, "cli-command");
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

        var config = await LauncherPaths.LoadResolvedAsync(configPath);
        if (repair) config.RepairMode = true;
        if (noLaunch) config.LaunchAfterUpdate = false;

        if (config.IsManagedDeployment)
        {
            var managedResponse = await new ManagedAgentClient().SendAsync(repair ? "repair" : "update", config.ProjectId);
            PrintAgentResponse(managedResponse);
            if (!managedResponse.Success) return 1;
            if (!noLaunch) _ = await ManagedAppLauncher.LaunchAsync(config);
            return 0;
        }

        var fileLogger = new FileLogger(config.LogDir);
        var reporter = new ConsoleProgressReporter();
        // Route both catalog resolution and the engine through the reporter so the CLI shows a single
        // in-place progress bar (interactive) or plain lines (redirected/service); engine console echo off.
        try
        {
            await ResolveCatalogForCliAsync(config, (stage, message, percent) => reporter.Report(new LauncherProgress(stage, message, percent)));
            using var engine = new LauncherEngine(config, reporter.Report, fileLogger, echoToConsole: false);
            await engine.RunAsync();
        }
        finally
        {
            reporter.Finish(); // close an open in-place bar line even if the run threw mid-download
        }
        return 0;
    }

    private static async Task<int> RunAgentClientAsync(string[] args)
    {
        var command = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "status";
        var endpoint = Get(args, "--endpoint");
        var projectId = Get(args, "--project");
        var response = await new ManagedAgentClient(endpoint).SendAsync(command, projectId);
        PrintAgentResponse(response);
        if (!string.IsNullOrWhiteSpace(response.ClientIdentity)) Console.WriteLine($"Client: {response.ClientIdentity}");
        return response.Success ? 0 : 1;
    }

    private static void PrintAgentResponse(ManagedAgentResponse response)
    {
        foreach (var progress in response.Progress)
            Console.WriteLine(progress.Percent.HasValue ? $"[{progress.Stage}] {progress.Message} ({progress.Percent:0}%)" : $"[{progress.Stage}] {progress.Message}");
        Console.WriteLine($"Agent {response.Status}: {response.Message}");
        Console.WriteLine($"Version: {response.AgentVersion}");
    }

    private static int RunCredentialCommand(string[] args)
    {
        var action = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "status";
        var name = Get(args, "--name") ?? throw new ArgumentException("credential requires --name <credential-name>.");
        if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(CredentialStore.Exists(name) ? $"Credential '{name}' is configured." : $"Credential '{name}' is not configured.");
            return CredentialStore.Exists(name) ? 0 : 1;
        }
        if (action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            CredentialStore.Delete(name);
            Console.WriteLine($"Credential '{name}' was deleted.");
            return 0;
        }
        if (!action.Equals("set", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown credential action: {action}.");

        var token = Environment.GetEnvironmentVariable("UE_DT_CREDENTIAL_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) token = ReadSecretFromConsole("Bearer token: ");
        CredentialStore.Save(name, token);
        Console.WriteLine($"Credential '{name}' was stored. The token value will not be displayed.");
        return 0;
    }

    private static async Task<int> PublishReleaseAsync(string[] args)
    {
        var options = new AtomicReleasePublishOptions
        {
            PackageDir = Required(args, "--package-dir"),
            ServerRoot = Required(args, "--server-root"),
            BaseUrlRoot = Required(args, "--base-url-root"),
            ProjectId = Required(args, "--project-id"),
            DisplayName = Get(args, "--display-name") ?? Required(args, "--project-id"),
            Version = Required(args, "--version"),
            Environment = Get(args, "--environment") ?? "prod",
            Channel = Get(args, "--channel") ?? "stable",
            Platform = Required(args, "--platform"),
            EntryPoint = Required(args, "--entry-point"),
            CatalogProfile = Get(args, "--catalog-profile") ?? "general",
            AllowedClientProfiles = (Get(args, "--allowed-profiles") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            Notes = Get(args, "--notes"),
            SetLatest = Has(args, "--set-latest"),
            DryRun = Has(args, "--dry-run"),
            ReplaceExisting = Has(args, "--replace"),
            AllowUnsigned = Has(args, "--allow-unsigned"),
            PrivateKeyPath = Get(args, "--private-key"),
            SigningKeyId = Get(args, "--key-id")
        };
        var report = await AtomicReleasePublisher.PublishAsync(options);
        Console.WriteLine(JsonSerializer.Serialize(report, JsonFiles.Options));
        return 0;
    }

    private static async Task<int> GenerateSigningKeyAsync(string[] args)
    {
        var privateKeyPath = Required(args, "--private-key");
        var publicKeyPath = Required(args, "--public-key");
        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
            throw new IOException("Signing key output already exists; refusing to overwrite it.");
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(privateKeyPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicKeyPath))!);
        await File.WriteAllTextAsync(privateKeyPath, key.ExportECPrivateKeyPem());
        await File.WriteAllTextAsync(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Signing key pair generated. Keep private key offline: {privateKeyPath}");
        Console.WriteLine($"Public key: {publicKeyPath}");
        return 0;
    }

    private static async Task<int> RunDoctorAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var report = await LauncherDoctor.RunAsync(configPath, Has(args, "--online"));
        Console.WriteLine(JsonSerializer.Serialize(report, JsonFiles.Options));
        return report.Healthy ? 0 : 1;
    }

    private static async Task<int> RunDiagnosticsAsync(string[] args)
    {
        var action = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "export";
        if (!action.Equals("export", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("diagnostics supports only the export action.");
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var output = Get(args, "--output") ?? $"launcher-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        Console.WriteLine("Diagnostics written: " + await DiagnosticsExporter.ExportAsync(configPath, output));
        return 0;
    }

    private static string ReadSecretFromConsole(string prompt)
    {
        Console.Write(prompt);
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        Console.WriteLine();
        return builder.ToString();
    }

    private static async Task<int> RunServiceAsync(string[] args)
    {
        var configPath = Get(args, "--config") ?? "launcher.config.json";
        var once = Has(args, "--once");
        var managedConfig = await LauncherPaths.LoadResolvedAsync(configPath);
        if (managedConfig.IsManagedDeployment)
        {
            if (!once) throw new InvalidOperationException("Managed deployment service loop is owned by UeDtLauncher.Agent. Use service --once for a manual cycle.");
            var managedResponse = await new ManagedAgentClient().SendAsync("service-run", managedConfig.ProjectId);
            PrintAgentResponse(managedResponse);
            return managedResponse.Success ? 0 : 1;
        }
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
        var config = await LauncherPaths.LoadResolvedAsync(configPath);
        if (config.IsManagedDeployment)
        {
            var managedResponse = await new ManagedAgentClient().SendAsync("rollback", config.ProjectId);
            PrintAgentResponse(managedResponse);
            return managedResponse.Success ? 0 : 1;
        }
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
        using var instanceLock = SingleInstanceLock.Acquire(LauncherPaths.UpdateLockPath(config));
        Console.WriteLine($"Rolling back using backup {Path.GetFileName(selected.BackupRoot)}...");
        await BackupManager.RestoreAsync(selected.BackupRoot, config.InstallDir, config.InstalledManifestPath, config.InstallStatePath, message =>
        {
            Console.WriteLine("[Rollback] " + message);
            fileLogger.Log("Rollback", message);
        });
        Console.WriteLine("Rollback completed.");
        return 0;
    }

    private static async Task ResolveCatalogForCliAsync(LauncherConfig config, Action<string, string, double?>? log = null)
    {
        log ??= (stage, message, percent) =>
            Console.WriteLine(percent.HasValue ? $"[{stage}] {message} ({percent:0}%)" : $"[{stage}] {message}");
        using var httpClient = SecureHttpClientFactory.Create(config);
        await CatalogResolver.ResolveAsync(config, httpClient, log);
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

        KnownValues.ValidatePlatform(platform);
        KnownValues.ValidateChannel(channel);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine($"WARNING: --base-url should be an absolute http(s) URL the client can reach (got '{baseUrl}'). Manifest file URLs are built from it.");
        }

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

        KnownValues.ValidateReleaseTuple(platform, environment, channel);

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

    private static async Task<int> ListReleasesAsync(string[] args)
    {
        var catalogPath = Required(args, "--catalog");
        var projectFilter = Get(args, "--project");
        var catalog = await JsonFiles.ReadAsync<DistributionCatalog>(catalogPath);

        var projects = catalog.Projects
            .Where(p => string.IsNullOrWhiteSpace(projectFilter) || string.Equals(p.ProjectId, projectFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projects.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(projectFilter) ? "Catalog has no projects." : $"Project not found: {projectFilter}");
            return 0;
        }

        foreach (var project in projects)
        {
            Console.WriteLine($"{project.ProjectId}  ({project.DisplayName})  — {project.Releases.Count} release(s)");
            foreach (var r in project.Releases.OrderBy(r => r.Environment).ThenBy(r => r.Channel).ThenBy(r => r.Platform).ThenBy(r => r.Version))
            {
                var latest = r.IsLatest ? " [latest]" : string.Empty;
                Console.WriteLine($"  {r.Version,-14} {r.Environment,-5}/{r.Channel,-7}/{r.Platform,-12} profiles=[{string.Join(",", r.AllowedClientProfiles)}]{latest}");
            }
        }

        return 0;
    }

    private static async Task<int> GenerateNginxAclAsync(string[] args)
    {
        var allowlistPath = Required(args, "--allowlist");
        var output = Get(args, "--output");
        var allowlist = await JsonFiles.ReadAsync<ProjectIpAllowlist>(allowlistPath);
        var config = NginxAclGenerator.Generate(allowlist);

        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(config);
        }
        else
        {
            await File.WriteAllTextAsync(output, config);
            Console.WriteLine($"Nginx per-project IP ACL written: {output}");
            Console.WriteLine("Include it ABOVE the generic /projects/ location blocks, then: sudo nginx -t && sudo systemctl reload nginx");
        }

        return 0;
    }

    private static async Task<int> SignManifestAsync(string[] args)
    {
        var manifestPath = Required(args, "--manifest");
        var privateKeyPath = Required(args, "--private-key");
        var keyId = Get(args, "--key-id");
        var output = Get(args, "--output") ?? manifestPath + ".sig";
        var payload = await File.ReadAllTextAsync(manifestPath);
        var privateKey = await File.ReadAllTextAsync(privateKeyPath);
        var signature = ManifestSignatureVerifier.Sign(payload, privateKey);
        if (string.IsNullOrWhiteSpace(keyId))
        {
            await File.WriteAllTextAsync(output, signature);
        }
        else
        {
            await JsonFiles.WriteAsync(output, new DetachedSignatureEnvelope { KeyId = keyId, Signature = signature });
        }
        Console.WriteLine($"Manifest signature written: {output}");
        return 0;
    }

    private static async Task<int> WriteSampleConfigAsync(string[] args)
    {
        var output = Get(args, "--output") ?? "launcher.config.json";
        var config = new LauncherConfig
        {
            SchemaVersion = 2,
            DeploymentMode = "managed-agent",
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
            RequireSignedManifests = true,
            Security = new LauncherSecurityConfig
            {
                CredentialName = "ue-dt-prod",
                AllowedDownloadHosts = { "updates.example.com", "your-update-server.example.com" },
                TrustedSigningKeys =
                {
                    new TrustedSigningKey { KeyId = "prod-2026", PublicKeyPath = "manifest-public-key.pem" }
                }
            },
            InstallDir = "app",
            StateRootDir = ".state",
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
                StartupGraceSeconds = 5,
                HealthCheckUrl = null,
                HealthCheckTimeoutSeconds = 60,
                RollbackOnHealthCheckFailure = true,
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
        Console.WriteLine("  gui                 (also: --gui forces GUI; --cli forces CLI -> defaults to 'run')");
        Console.WriteLine("  sample-config --output launcher.config.json");
        Console.WriteLine("  generate-manifest --package-dir <dir> --base-url <url> --entry-point <relative path> --version <version> [--app-id <id>] --output <manifest.json>");
        Console.WriteLine("  update-catalog --catalog <catalog.json> --project-id <id> --version <version> --environment <prod|dev> --channel <stable|beta|dev> --platform <windows-x64|linux-x64> --manifest-url <url> [--display-name <name>] [--allowed-profiles general,developer] [--notes <text>] [--set-latest] [--remove] [--remove-project-if-empty]");
        Console.WriteLine("  list-releases --catalog <catalog.json> [--project <id>]");
        Console.WriteLine("  generate-nginx-acl --allowlist <project-ip-allowlist.json> [--output <acl.conf>]");
        Console.WriteLine("  sign-manifest --manifest <manifest.json> --private-key <private.pem> [--key-id <id>] --output <manifest.json.sig>");
        Console.WriteLine("  run --config launcher.config.json [--repair] [--no-launch]");
        Console.WriteLine("  service --config launcher.config.json [--interval <seconds>] [--once]");
        Console.WriteLine("  rollback --config launcher.config.json [--list] [--backup <timestamp>]");
        Console.WriteLine("  agent [status] [--endpoint <pipe-or-socket>] [--project <id>]");
        Console.WriteLine("  credential <set|status|delete> --name <credential-name>");
        Console.WriteLine("  publish-release --package-dir <dir> --server-root <dir> --base-url-root <url> --project-id <id> --version <version> --platform <platform> --entry-point <path> --private-key <pem> --key-id <id> [--dry-run] [--replace] [--set-latest]");
        Console.WriteLine("  generate-signing-key --private-key <private.pem> --public-key <public.pem>");
        Console.WriteLine("  doctor --config launcher.config.json [--online]");
        Console.WriteLine("  diagnostics export --config launcher.config.json [--output <diagnostics.zip>]");
    }
}
