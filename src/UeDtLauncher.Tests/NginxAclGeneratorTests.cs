using Xunit;

namespace UeDtLauncher.Tests;

public class NginxAclGeneratorTests
{
    [Fact]
    public void Generate_EmitsPerProjectAllowDenyBlocks()
    {
        var allowlist = new ProjectIpAllowlist
        {
            Projects = new Dictionary<string, List<string>>
            {
                ["m7at10-dt"] = new() { "10.10.20.0/24", "172.18.45.7" },
                ["ue-dt-simulator"] = new() { "10.20.0.0/16" }
            }
        };

        var conf = NginxAclGenerator.Generate(allowlist);

        Assert.Contains("location ~ ^/projects/m7at10-dt/ {", conf);
        Assert.Contains("allow 10.10.20.0/24;", conf);
        Assert.Contains("allow 172.18.45.7;", conf);
        Assert.Contains("location ~ ^/projects/ue-dt-simulator/ {", conf);
        Assert.Contains("allow 10.20.0.0/16;", conf);
        Assert.Contains("deny all;", conf);
        Assert.Contains("try_files $uri =404;", conf);
    }

    [Fact]
    public void Generate_RejectsInvalidCidr()
    {
        var allowlist = new ProjectIpAllowlist
        {
            Projects = new Dictionary<string, List<string>> { ["p"] = new() { "999.1.1.0/24" } }
        };
        Assert.Throws<ArgumentException>(() => NginxAclGenerator.Generate(allowlist));
    }

    [Fact]
    public void Generate_RejectsEmptyProjectEntries()
    {
        var allowlist = new ProjectIpAllowlist
        {
            Projects = new Dictionary<string, List<string>> { ["p"] = new() }
        };
        Assert.Throws<ArgumentException>(() => NginxAclGenerator.Generate(allowlist));
    }

    [Fact]
    public void Generate_RejectsEmptyAllowlist()
    {
        Assert.Throws<ArgumentException>(() => NginxAclGenerator.Generate(new ProjectIpAllowlist()));
    }

    [Fact]
    public void ExampleAllowlistFile_GeneratesValidConfig()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "examples", "project-ip-allowlist.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);

        var allowlist = JsonFiles.ReadAsync<ProjectIpAllowlist>(Path.Combine(dir!, "examples", "project-ip-allowlist.json")).GetAwaiter().GetResult();
        var conf = NginxAclGenerator.Generate(allowlist);
        Assert.Contains("location ~ ^/projects/m7at10-dt/ {", conf);
    }
}
