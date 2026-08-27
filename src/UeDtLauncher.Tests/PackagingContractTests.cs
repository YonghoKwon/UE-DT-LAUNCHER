using System.Reflection;
using Xunit;

namespace UeDtLauncher.Tests;

public class PackagingContractTests
{
    [Fact]
    public void DevelopmentAssembly_IsExplicitlyMarkedUnsigned()
    {
        var version = typeof(UeDtLauncher.Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(version);
        Assert.Contains("unsigned-dev", version!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsInstaller_DeclaresMachineAgentAndUnsignedMarker()
    {
        var root = RepositoryRoot();
        var product = File.ReadAllText(Path.Combine(root, "installer", "windows", "Product.wxs"));
        Assert.Contains("Scope=\"perMachine\"", product);
        Assert.Contains("Name=\"UeDtLauncherAgent\"", product);
        Assert.Contains("Account=\"NT AUTHORITY\\LocalService\"", product);
        Assert.Contains("BUILD-INFO.txt", product);
    }

    [Fact]
    public void Rpm_PreservesDotNetBundleAndConfigurationOnUpgrade()
    {
        var root = RepositoryRoot();
        var spec = File.ReadAllText(Path.Combine(root, "packaging", "linux", "ue-dt-launcher.spec"));
        Assert.Contains("%global __os_install_post %{nil}", spec);
        Assert.Contains("%config(noreplace)", spec);
        Assert.Contains("ue-dt-launcher-agent.service", spec);
    }

    [Fact]
    public void StableReleaseWorkflow_FailsClosedWithoutBothSigningSystems()
    {
        var root = RepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        Assert.Contains("WINDOWS_SIGNING_PFX_BASE64", workflow);
        Assert.Contains("RPM_GPG_PRIVATE_KEY", workflow);
        Assert.Contains("-RequireSignature", workflow);
        Assert.Contains("rpmsign --addsign", workflow);
    }

    private static string RepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(current, "UeDtLauncher.sln")))
            current = Path.GetDirectoryName(current) ?? throw new DirectoryNotFoundException("Repository root not found.");
        return current;
    }
}
