namespace UeDtLauncher;

public sealed record DistributionTemplateOptions(
    string ServerUrl = "http://10.20.30.40", string ProjectId = "ue-dt-simulator", string Profile = "general",
    string? Platform = null, string DeploymentMode = "managed-agent", string AuthenticationMode = "request-signature-v1",
    string CredentialName = "ue-dt-device", string SigningKeyId = "release-1", string PublicKeyPath = "release-public.pem");

public static class LauncherConfigurationTemplates
{
    public static LauncherConfig Distribution(DistributionTemplateOptions options)
    {
        var platform = options.Platform ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64");
        KnownValues.ValidateReleaseTuple(platform, "prod", "stable"); ReleaseSidecar.Segment(options.ProjectId);
        DeviceCredentials.ValidateIdentifier(options.CredentialName); DeviceCredentials.ValidateIdentifier(options.SigningKeyId);
        if (options.Profile is not ("general" or "developer") || options.DeploymentMode is not ("managed-agent" or "portable")) throw new ArgumentException("Choose general/developer and managed-agent/portable.");
        var origin = new Uri(options.ServerUrl, UriKind.Absolute);
        if (origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new ArgumentException("--server-url must contain only an HTTP(S) origin.");
        if (options.AuthenticationMode == "bearer" && origin.Scheme != "https") throw new ArgumentException("Bearer templates require HTTPS.");
        string apps = "app", state = ".state", logs = "logs";
        if (options.DeploymentMode == "managed-agent")
        {
            if (platform == "linux-x64") { apps = "/var/lib/ue-dt-launcher/apps"; state = "/var/lib/ue-dt-launcher/state"; logs = "/var/log/ue-dt-launcher"; }
            else
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Generate Windows managed paths on Windows to honor its ProgramData location.");
                var layout = ManagedLauncherPathLayout.Current(); apps = layout.AppsRoot; state = layout.StateRoot; logs = layout.LogRoot;
            }
        }
        var config = new LauncherConfig
        {
            SchemaVersion = options.AuthenticationMode == "request-signature-v1" ? 3 : 2,
            DistributionServerUrl = RequestSignatures.Origin(origin), CatalogUrl = RequestSignatures.Origin(origin) + "/api/v1/catalog",
            ManifestUrl = "", DeploymentMode = options.DeploymentMode, ProjectId = options.ProjectId,
            ClientProfile = options.Profile, TargetPlatform = platform, RequireSignedManifests = true,
            InstallDir = apps, StateRootDir = state, LogDir = logs, ServiceMode = null,
            Security = new()
            {
                AuthenticationMode = options.AuthenticationMode, RequireHttps = origin.Scheme == "https", CredentialName = options.CredentialName,
                AllowedDownloadHosts = [origin.Host], TrustedSigningKeys = [new() { KeyId = options.SigningKeyId, PublicKeyPath = options.PublicKeyPath }]
            }
        };
        LauncherConfigValidator.Validate(config); return config;
    }
}
