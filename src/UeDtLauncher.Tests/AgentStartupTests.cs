using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class AgentStartupTests : IDisposable
{
    private readonly DirectoryInfo work = Directory.CreateTempSubdirectory("uedt-agent-startup-");
    private string DataRoot => Path.Combine(work.FullName, "agent-data");

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("HELP")]
    public async Task HelpExitsBeforeHostInitializationAndLeavesFilesUntouched(string option)
    {
        // A host that attempts to load configuration from the working directory will fail.
        var config = Path.Combine(work.FullName, "appsettings.json");
        await File.WriteAllTextAsync(config, "{ invalid json");

        var result = await RunAgentAsync(option);

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains("UeDtLauncher.Agent --probe", result.Output);
        Assert.Contains("migrate --config <legacy launcher.config.json> [--dry-run]", result.Output);
        Assert.Contains("--apply is blocked", result.Output);
        Assert.Contains("UeDtLauncher.DistributionServer cleanup --config", result.Output);
        Assert.Equal(config, Assert.Single(Directory.EnumerateFileSystemEntries(work.FullName)));
        Assert.Equal("{ invalid json", await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task HelpTakesPrecedenceOverProbeAndMigration()
    {
        var result = await RunAgentAsync("migrate", "--config", "missing.json", "--probe", "-h");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.StartsWith("UE-DT Launcher Agent", result.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.FullName));
    }

    [Fact]
    public async Task ProbeStillReturnsRuntimeInformation()
    {
        var result = await RunAgentAsync("--probe");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var info = JsonDocument.Parse(result.Output);
        Assert.Equal("UE-DT Launcher Agent", info.RootElement.GetProperty("Product").GetString());
        Assert.True(info.RootElement.GetProperty("ProcessId").GetInt32() > 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(work.FullName));
    }

    [Fact]
    public async Task MigrationStillPreviewsByDefaultAndRefusesApply()
    {
        var config = Path.Combine(work.FullName, "source.json");
        const string source = """{"projectId":"demo","stateRootDir":"state"}""";
        await File.WriteAllTextAsync(config, source);

        var plan = await RunAgentAsync("migrate", "--config", config);
        var dryRun = await RunAgentAsync("migrate", "--config", config, "--dry-run");
        var apply = await RunAgentAsync("migrate", "--config", config, "--apply");

        Assert.Equal(0, plan.Code);
        Assert.Equal(0, dryRun.Code);
        Assert.Equal(plan.Output, dryRun.Output);
        using var document = JsonDocument.Parse(plan.Output);
        Assert.False(document.RootElement.GetProperty("CanApply").GetBoolean());
        Assert.False(document.RootElement.GetProperty("Applied").GetBoolean());
        Assert.Equal(1, apply.Code);
        Assert.Empty(apply.Output);
        Assert.Contains("Shared installation ownership transfer is not supported", apply.Error);
        Assert.Equal(config, Assert.Single(Directory.EnumerateFileSystemEntries(work.FullName)));
        Assert.Equal(source, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task NonHelpHostArgumentsStillReachHostConfiguration()
    {
        await File.WriteAllTextAsync(Path.Combine(work.FullName, "appsettings.HelpTest.json"), "{ invalid json");

        var result = await RunAgentAsync("--environment", "HelpTest");

        Assert.NotEqual(0, result.Code);
        Assert.Contains("appsettings.HelpTest.json", result.Error);
        Assert.DoesNotContain("Show this help and exit", result.Output);
    }

    private async Task<(int Code, string Output, string Error)> RunAgentAsync(params string[] arguments)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "UeDtLauncher.sln")))
            repo = repo.Parent ?? throw new InvalidOperationException("Could not find UeDtLauncher.sln.");
        var configuration = typeof(AgentStartupTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var fileName = OperatingSystem.IsWindows() ? "UeDtLauncher.Agent.exe" : "UeDtLauncher.Agent";
        var executable = Path.Combine(repo.FullName, "src", "UeDtLauncher.Agent", "bin",
            configuration, "net8.0", RuntimeInformation.RuntimeIdentifier, fileName);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = work.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["UE_DT_AGENT_DATA_ROOT"] = DataRoot;
        start.Environment["UE_DT_AGENT_ENDPOINT"] = OperatingSystem.IsWindows()
            ? work.Name
            : Path.Combine(work.FullName, "agent.sock");
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test Agent.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose() => work.Delete(recursive: true);
}
