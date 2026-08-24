using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UeDtLauncher.Agent;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(AgentRuntimeInfo.Current()));
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
        });
        builder.Services.AddHostedService<AgentWorker>();

        await builder.Build().RunAsync();
        return 0;
    }
}

public sealed record AgentRuntimeInfo(
    string Product,
    string Version,
    string Platform,
    int ProcessId)
{
    public static AgentRuntimeInfo Current() => new(
        "UE-DT Launcher Agent",
        typeof(AgentRuntimeInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unknown",
        Environment.ProcessId);
}

internal sealed class AgentWorker(ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtime = AgentRuntimeInfo.Current();
        logger.LogInformation("{Product} {Version} started on {Platform} (pid {ProcessId})",
            runtime.Product,
            runtime.Version,
            runtime.Platform,
            runtime.ProcessId);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        logger.LogInformation("{Product} stopped", runtime.Product);
    }
}
