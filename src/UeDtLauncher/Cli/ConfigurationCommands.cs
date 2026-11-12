using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> WriteSampleConfigAsync(string[] args)
    {
        var output = Get(args, "--output") ?? "launcher.config.json";
        if (File.Exists(output) && !Has(args, "--force")) throw new IOException("Config already exists; use a new output or explicit --force.");
        var mode = Get(args, "--mode") ?? "distribution";
        if(mode=="managed-client")
        {
            var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"--mode","--project-id","--platform","--environment","--channel","--version-policy","--version","--output","--force"};
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<args.Length;i++)
            {
                if(!allowed.Contains(args[i]) || !seen.Add(args[i]))throw new ArgumentException("Unsupported or duplicate managed-client option. Operational settings belong in the Agent configuration.");
                if(!args[i].Equals("--force",StringComparison.OrdinalIgnoreCase))
                {if(i+1>=args.Length || args[i+1].StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("Missing managed-client option value.");i++;}
            }
            var client=LauncherConfigurationTemplates.ManagedClient(Required(args,"--project-id"),Get(args,"--platform"),
                Get(args,"--environment")??"prod",Get(args,"--channel")??"stable",Get(args,"--version-policy")??"latest",Get(args,"--version"));
            await JsonFiles.WriteAsync(output,client,overwrite:Has(args,"--force"));
            Console.WriteLine("Managed client display configuration written. Provision the Agent operational configuration separately; the client edition is fixed by its build.");
            return 0;
        }
        if (mode == "distribution")
        {
            var distribution = LauncherConfigurationTemplates.Distribution(new DistributionTemplateOptions(
                ServerUrl: Get(args, "--server-url") ?? "http://10.20.30.40", ProjectId: Get(args, "--project-id") ?? "ue-dt-simulator",
                Profile: Get(args, "--profile") ?? "general", Platform: Get(args, "--platform"),
                DeploymentMode: Get(args, "--deployment-mode") ?? "managed-agent", AuthenticationMode: Get(args, "--auth") ?? "request-signature-v1",
                CredentialName: Get(args, "--credential-name") ?? "ue-dt-device", SigningKeyId: Get(args, "--signing-key-id") ?? "release-1",
                PublicKeyPath: Get(args, "--public-key") ?? "release-public.pem"));
            await JsonFiles.WriteAsync(output, distribution);
            Console.WriteLine("Distribution config written. Provision the device key and trusted release public key, then run doctor --online. HTTP is not encrypted.");
            return 0;
        }
        if (mode != "legacy-catalog") throw new ArgumentException("--mode must be distribution, managed-client or legacy-catalog.");
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

}
