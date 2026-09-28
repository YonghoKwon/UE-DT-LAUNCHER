using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ConfigurationTemplateTests
{
    [Theory]
    [InlineData("general")]
    [InlineData("developer")]
    public void DistributionTemplateUsesExplicitSignedHttpAndManagedLinuxPaths(string profile)
    {
        var config = LauncherConfigurationTemplates.Distribution(new(Profile: profile, Platform: "linux-x64"));
        Assert.Equal(3, config.SchemaVersion); Assert.Equal("request-signature-v1", config.Security.AuthenticationMode);
        Assert.False(config.Security.RequireHttps); Assert.True(config.RequireSignedManifests);
        Assert.Equal("/var/lib/ue-dt-launcher/apps", config.InstallDir); Assert.Equal("/api/v1/catalog", new Uri(config.CatalogUrl!).AbsolutePath);
        Assert.Null(config.ServiceMode); LauncherConfigValidator.Validate(config);
    }

    [Fact]
    public void ExistingHttpsBearerModeIsAvailable_ButHttpBearerIsNot()
    {
        var config = LauncherConfigurationTemplates.Distribution(new(ServerUrl: "https://updates.example.com", AuthenticationMode: "bearer", DeploymentMode: "portable"));
        Assert.Equal(2, config.SchemaVersion); Assert.True(config.Security.RequireHttps);
        Assert.Throws<ArgumentException>(() => LauncherConfigurationTemplates.Distribution(new(AuthenticationMode: "bearer")));
        Assert.Throws<ArgumentException>(() => LauncherConfigurationTemplates.Distribution(new(ServerUrl: "http://user:password@localhost")));
    }

    [Fact]
    public async Task MissingPublicKeyIsNotHealthy_EvenWithNoKeysToEnumerate()
    {
        var root = Path.Combine(Path.GetTempPath(), "uedt-template-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var config = new LauncherConfig { SchemaVersion = 2, DeploymentMode = "portable", RequireSignedManifests = true, ManifestUrl = "https://example.com/manifest", Security = new() { CredentialName = "not-configured" } };
            var path = Path.Combine(root, "config.json"); await JsonFiles.WriteAsync(path, config);
            var report = await LauncherDoctor.RunAsync(path, false, agentContext: true);
            Assert.False(report.Healthy); Assert.Contains(report.Checks, c => c.Name == "signing-keys" && !c.Success);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SampleCommandDoesNotOverwriteExistingConfiguration()
    {
        var path = Path.Combine(Path.GetTempPath(), "uedt-sample-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "preserve-me");
            Assert.Equal(1, await UeDtLauncher.Program.MainAsync(["sample-config", "--output", path]));
            Assert.Equal("preserve-me", await File.ReadAllTextAsync(path));
            Assert.Equal(0, await UeDtLauncher.Program.MainAsync(["sample-config", "--output", path, "--platform", "linux-x64", "--force"]));
            var config = JsonSerializer.Deserialize<LauncherConfig>(await File.ReadAllTextAsync(path), JsonFiles.Options)!;
            Assert.Equal(3, config.SchemaVersion); Assert.Equal("managed-agent", config.DeploymentMode);
        }
        finally { File.Delete(path); }
    }
}
