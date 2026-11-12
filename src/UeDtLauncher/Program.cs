using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using UeDtLauncher.Gui;

namespace UeDtLauncher;

public static partial class Program
{
    private static readonly string[] KnownSubcommands =
    {
        "run", "service", "rollback", "runtime", "generate-manifest", "update-catalog", "release-metadata", "import-install",
        "list-releases", "generate-nginx-acl", "sign-manifest", "generate-signing-key", "sample-config", "publish-release", "doctor", "diagnostics", "agent", "credential", "operation", "scheduled-check"
    };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "runtime-host", "--capability-probe" })) return RuntimeHost.RunProbe();
        if (args.SequenceEqual(new[] { "runtime-host" })) return RuntimeHost.RunSessionAsync().GetAwaiter().GetResult();
        CrashReporter.Install(Path.Combine(AppContext.BaseDirectory, "logs"));
        if (args.SequenceEqual(new[] { "--build-info" }))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { version=LauncherBuildInfo.Version, edition=LauncherBuildInfo.Edition.ToString(), platform=LauncherBuildInfo.Platform },JsonFiles.Options));
            return 0;
        }
        if (args.Length == 1 && args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
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

        var diagnosticCommand = !isGui && CliArgs(args, wantsCli).FirstOrDefault() is "doctor" or "diagnostics" or "scheduled-check" or "operation";
        if (File.Exists(SelfUpdateManager.PendingFilePath)) Console.Error.WriteLine(LauncherBuildInfo.UpdateNotice);

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

    internal static async Task<int> MainAsync(string[] args)
    {
        try
        {
            CliArguments.Validate(args, ValueOptions);
            if (Has(args, "--help") || Has(args, "-h"))
            {
                PrintHelp();
                return 0;
            }

            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
            return command switch
            {
                "release-metadata" => await GenerateSidecarAsync(args.Skip(1).ToArray()),
                "import-install" => await ImportInstallAsync(args.Skip(1).ToArray()),
                "run" => await RunLauncherAsync(args.Skip(1).ToArray()),
                "operation" => await RunOperationAsync(args.Skip(1).ToArray()),
                "scheduled-check" => await RunScheduledCheckAsync(args.Skip(1).ToArray()),
                "runtime" => await RunRuntimeCommandAsync(args.Skip(1).ToArray()),
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
        catch (CliArgumentException ex)
        {
            var supportId = CliErrors.Write(Console.Error, ex, Has(args, "--error-details"));
            CrashReporter.Report(ex, "cli-arguments:" + supportId);
            return 2;
        }
        catch (Exception ex)
        {
            var supportId = CliErrors.Write(Console.Error, ex, Has(args, "--error-details"));
            CrashReporter.Report(ex, "cli-command:" + supportId);
            return 1;
        }
    }

}
