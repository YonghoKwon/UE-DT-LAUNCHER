using System.Diagnostics;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record RuntimeHostSession(LauncherConfig Config, RuntimeLaunchTicket Ticket, string? AgentEndpoint, ReleaseSelection? Selection = null);

public static class RuntimeLauncher
{
    public static string HostExecutable()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Published launcher path is unavailable.");
        var host = Path.Combine(Path.GetDirectoryName(current)!, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
        if (!File.Exists(host)) throw new InvalidOperationException("A published launcher/runtime-host is required alongside the Agent.");
        return host;
    }

    public static async Task<Process> LaunchAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        RuntimeLaunchTicket ticket;
        if (config.IsManagedDeployment)
        {
            var response = await new ManagedAgentClient().SendRuntimeAsync("launch-begin", config, cancellationToken: cancellationToken);
            if (!response.Success || response.RuntimeTicket is null) throw new RuntimeBlockedException(response.Runtime ?? new(RuntimeState.Unknown, response.Status, response.Message));
            ticket = response.RuntimeTicket;
            if (config.SelectedRelease is not null && config.SelectedRelease != response.SelectedRelease) throw new InvalidDataException("Runtime release selection mismatch.");
            var installedHost = Path.Combine(ManagedLauncherPathLayout.Current().InstallRoot, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
            if (!SafePath.FileSystemComparer.Equals(ticket.HostExecutable, Path.GetFullPath(installedHost))) throw new InvalidDataException("Runtime host must use the installed launcher beside the Agent.");
        }
        else ticket = RuntimeStore.Begin(config, RuntimeIdentities.Current(), HostExecutable());
        return await StartHostAsync(new RuntimeHostSession(config, ticket, config.IsManagedDeployment ? ManagedAgentProtocol.ResolveEndpoint() : null, config.SelectedRelease), cancellationToken);
    }

    internal static Task<Process> LaunchServiceAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        RuntimeServiceState.RequireLaunch(config, true);
        var ticket = RuntimeStore.BeginUnderServiceLock(config, RuntimeIdentities.Current(), HostExecutable());
        return StartHostAsync(new(config, ticket, null, config.SelectedRelease), cancellationToken);
    }

    private static async Task<Process> StartHostAsync(RuntimeHostSession input, CancellationToken cancellationToken)
    {
        var ticket = input.Ticket;
        var info = new ProcessStartInfo(ticket.HostExecutable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("runtime-host");
        SanitizeEnvironment(info);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Runtime host did not start; launch state requires inspection.");
        // Ticket goes through a private inherited pipe, never the command line or logs.
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(input, JsonFiles.Options).Replace("\r", "").Replace("\n", ""));
        process.StandardInput.Close();
        var ready = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        if (ready is null || !ready.Contains("\"state\":\"started\"", StringComparison.Ordinal))
            throw new InvalidOperationException("실행 상태를 확인할 수 없습니다. 일부 프로그램이 실행됐을 수 있으므로 관리자 점검이 필요합니다.");
        return process;
    }

    internal static void SanitizeEnvironment(ProcessStartInfo info)
    {
        foreach (var name in info.Environment.Keys.ToArray())
        {
            if (name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || name is "DOTNET_STARTUP_HOOKS" or "DOTNET_ADDITIONAL_DEPS" or "DOTNET_SHARED_STORE" or "LD_PRELOAD" or "LD_LIBRARY_PATH")
                info.Environment.Remove(name);
        }
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
    }
}
