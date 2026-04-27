namespace UeDtLauncher;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            return command switch
            {
                "run" => await RunLauncherAsync(args.Skip(1).ToArray()),
                "generate-manifest" => await GenerateManifestAsync(args.Skip(1).ToArray()),
                "sample-config" => await WriteSampleConfigAsync(args.Skip(1).ToArray()),
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
        if (repair)
        {
            config.RepairMode = true;
        }
        if (noLaunch)
        {
            config.LaunchAfterUpdate = false;
        }

        await new LauncherEngine(config).RunAsync();
        return 0;
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

        await ManifestGenerator.GenerateAsync(packageDir, output, baseUrl, entryPoint, version, channel, platform);
        return 0;
    }

    private static async Task<int> WriteSampleConfigAsync(string[] args)
    {
        var output = Get(args, "--output") ?? "launcher.config.json";
        var config = new LauncherConfig
        {
            ManifestUrl = "https://your-update-server.example.com/windows-x64/manifest.json",
            InstallDir = "app",
            StagingDir = ".staging",
            BackupDir = ".backup",
            InstalledManifestPath = "installed-manifest.json",
            LaunchAfterUpdate = true,
            RepairMode = false,
            RemoveFilesNotInManifest = false,
            MaxRetryCount = 3,
            HttpTimeoutSeconds = 300,
            LaunchArguments = new[] { "-log" }
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
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument: {name}");
        }
        return value;
    }

    private static string? Get(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static bool Has(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static void PrintHelp()
    {
        Console.WriteLine("UE-DT-LAUNCHER");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  sample-config --output launcher.config.json");
        Console.WriteLine("  generate-manifest --package-dir <dir> --base-url <url> --entry-point <relative path> --version <version> --output <manifest.json>");
        Console.WriteLine("  run --config launcher.config.json [--repair] [--no-launch]");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  UeDtLauncher run --config launcher.config.json");
    }
}
