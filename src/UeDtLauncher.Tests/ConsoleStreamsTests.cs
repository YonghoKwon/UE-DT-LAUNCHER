using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ConsoleStreamsTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "console-stream-tests-" + Guid.NewGuid().ToString("N"));
    private static string Root
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "UeDtLauncher.sln"))) path = path.Parent;
            return path!.FullName;
        }
    }
    private static string Configuration => typeof(ConsoleStreamsTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
    private static string Harness => Path.Combine(Root, "tools/ConsoleStreamHarness/bin", Configuration, "net8.0/ConsoleStreamHarness.dll");
    private static string Launcher => Path.Combine(Root, "src/UeDtLauncher/bin", typeof(Program).Assembly.GetName().Name!.EndsWith(".Developer") ? "Developer" : "General", Configuration, "net8.0", typeof(Program).Assembly.GetName().Name + ".exe");

    private async Task<(int Code, string Output, string Error)> RunAsync(string executable, IEnumerable<string> arguments, string? input = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true), StandardInputEncoding = new UTF8Encoding(false) };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid")]
    [InlineData("closed")]
    public async Task UnusableStandardHandlesNeverReplaceExistingWriters(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(temporary); var report = Path.Combine(temporary, "guard.json");
        var process = await RunAsync("dotnet", [Harness, "guard", kind, report]);
        Assert.Equal(0, process.Code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.True(json.RootElement.GetProperty("outputWriterPreserved").GetBoolean());
        Assert.True(json.RootElement.GetProperty("errorWriterPreserved").GetBoolean());
    }

    [Fact]
    public async Task ValidPipesKeepLosslessUtf8Korean()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(temporary); var report = Path.Combine(temporary, "pipe.json");
        var process = await RunAsync("dotnet", [Harness, "guard", "pipe", report]);
        Assert.Equal(0, process.Code);
        Assert.Equal("한글 출력\n", process.Output.Replace("\r\n", "\n"));
        Assert.Equal("한글 오류\n", process.Error.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("control", "--help", "CONSOLE_POSITIVE_CONTROL")]
    [InlineData("native", "--help", "UE-DT-LAUNCHER")]
    [InlineData("native", "--version", "<version>")]
    [InlineData("native", "unknown-review-command", "Unknown command")]
    [InlineData("powershell", "--help", "UE-DT-LAUNCHER")]
    [InlineData("powershell", "--version", "<version>")]
    [InlineData("powershell", "unknown-review-command", "Unknown command")]
    [InlineData("cmd", "--help", "UE-DT-LAUNCHER")]
    [InlineData("cmd", "--version", "<version>")]
    [InlineData("cmd", "unknown-review-command", "Unknown command")]
    public async Task NonInheritedTerminalOutputReachesThePrivateConsoleScreen(string shell, string command, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(temporary); var report = Path.Combine(temporary, "screen.json");
        var process = await RunAsync("dotnet", [Harness, "console", Launcher, shell, report, command]);
        Assert.Equal(0, process.Code);
        Assert.Empty(process.Output); Assert.Empty(process.Error); // Target output was never captured by redirected pipes.
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var expectedText = expected == "<version>" ? typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion : expected;
        Assert.Contains(expectedText, json.RootElement.GetProperty("text").GetString());
        Assert.False(json.RootElement.GetProperty("standardHandlesInherited").GetBoolean());
        if (shell is "native" or "cmd") Assert.Equal(command == "unknown-review-command" ? 1 : 0, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("cmd-file")]
    [InlineData("powershell-file")]
    public async Task FileRedirectionKeepsUtf8AndTheRuntimeProtocolStaysOneJsonLine(string shell)
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(temporary); var prefix = Path.Combine(temporary, "native"); var report = prefix + ".json";
        var result = await RunAsync("dotnet", [Harness, "console", Launcher, shell, report, prefix, "run", "--config", Path.Combine(temporary, "missing.json")]);
        Assert.Equal(0, result.Code);
        var error = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(shell == "cmd-file" ? prefix + ".err" : prefix));
        Assert.Contains("지원 ID:", error);
        var runtime = await RunAsync(Launcher, ["runtime-host"], "{}\n");
        Assert.Equal(1, runtime.Code);
        Assert.Empty(runtime.Error);
        var line = Assert.Single(runtime.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(line); Assert.Equal("unknown", json.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task RuntimeSessionWithoutProtocolPipesDoesNotAttachToTheParentTerminal()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(temporary); var report = Path.Combine(temporary, "runtime-screen.json");
        var result = await RunAsync("dotnet", [Harness, "console", Launcher, "native", report, "runtime-host"]);
        Assert.Equal(0, result.Code);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Empty(json.RootElement.GetProperty("text").GetString()!);
        Assert.Equal(1, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(true, "--gui")]
    [InlineData(true, "gui")]
    [InlineData(false, "--help")]
    [InlineData(false, "--version")]
    [InlineData(false, "--build-info")]
    [InlineData(false, "--gui", "--cli")]
    [InlineData(false, "runtime-host")]
    public void GuiRoutingIsPureAndDoesNotStartOrAttachAConsole(bool expected, params string[] arguments)
        => Assert.Equal(expected, Program.StartsGui(arguments));

    public void Dispose() { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
}
