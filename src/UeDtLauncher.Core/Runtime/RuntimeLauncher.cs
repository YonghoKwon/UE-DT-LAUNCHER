using System.Diagnostics;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record RuntimeHostSession(LauncherConfig Config, RuntimeLaunchTicket Ticket, string? AgentEndpoint, ReleaseSelection? Selection = null);

public static class RuntimeLauncher
{
    internal static readonly AsyncLocal<Action<string>?> Boundary=new(); // Tests only; no product option activates it.
    public static string HostExecutable()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Published launcher path is unavailable.");
        var host = Path.Combine(Path.GetDirectoryName(current)!, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
        if (!File.Exists(host)) throw new InvalidOperationException("A published launcher/runtime-host is required alongside the Agent.");
        return host;
    }

    public static async Task<Process> LaunchAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeLaunchTicket ticket;
        if (config.IsManagedDeployment)
        {
            // Once sent, retain the ticket acknowledgement so cancellation can abort that exact unstarted attempt.
            var response = await new ManagedAgentClient().SendRuntimeAsync("launch-begin", config, cancellationToken: CancellationToken.None);
            if (!response.Success || response.RuntimeTicket is null) throw new RuntimeBlockedException(response.Runtime ?? new(RuntimeState.Unknown, response.Status, response.Message));
            ticket = response.RuntimeTicket;
            PinManagedSelection(config, response);
            response.ClientPresentation?.Validate(config.SelectedRelease,ticket.InstallationId);
            config.ManagedPresentation=response.ClientPresentation;
            var installedHost = Path.Combine(ManagedLauncherPathLayout.Current().InstallRoot, OperatingSystem.IsWindows() ? "UeDtLauncher.exe" : "UeDtLauncher");
            if (!SafePath.FileSystemComparer.Equals(ticket.HostExecutable, Path.GetFullPath(installedHost))) throw new InvalidDataException("Runtime host must use the installed launcher beside the Agent.");
        }
        else
        {
            await LaunchPolicy.VerifyOnlineAsync(config, cancellationToken);
            Boundary.Value?.Invoke("authorized");cancellationToken.ThrowIfCancellationRequested();
            ticket = RuntimeStore.Begin(config, RuntimeIdentities.Current(), HostExecutable());
        }
        try
        {
            var sessionConfig=config.IsManagedDeployment?ManagedClientContext.Create(config):config;
            sessionConfig.SelectedRelease=config.SelectedRelease;
            var process=await StartHostAsync(new RuntimeHostSession(sessionConfig, ticket, config.IsManagedDeployment ? ManagedAgentProtocol.ResolveEndpoint() : null, config.SelectedRelease), cancellationToken);
            if(config.IsManagedDeployment)
            {
                if(config.ManagedPresentation is not { } presentation)config.ManagedIntegrationWarning="바로가기와 설치 폴더 정보를 확인하려면 업데이트 서비스를 갱신해 주세요.";
                else
                {
                    try{WindowsIntegration.ApplyManaged(presentation,ticket.HostExecutable);}
                    catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidDataException)
                    {config.ManagedIntegrationWarning="프로그램은 실행됐습니다. 바로가기 생성 상태를 관리자에게 문의해 주세요.";}
                }
            }
            return process;
        }
        catch(OperationCanceledException)
        {
            if(config.IsManagedDeployment)(await new ManagedAgentClient().SendRuntimeAsync("launch-abort",config,ticket,cancellationToken:CancellationToken.None)).ThrowIfFailed();
            else RuntimeStore.AbortBeforeStart(config,ticket,RuntimeIdentities.Current());
            throw;
        }
    }

    internal static void PinManagedSelection(LauncherConfig config, ManagedAgentResponse response)
    {
        if (response.SelectedRelease is not { } selection)
        {
            if (config.SelectedRelease is not null || !string.IsNullOrWhiteSpace(config.DistributionServerUrl))
                throw new InvalidDataException("Runtime release selection is missing.");
            return; // Legacy direct-manifest mode.
        }
        selection.Validate();
        if (config.SelectedRelease is not null && config.SelectedRelease != selection) throw new InvalidDataException("Runtime release selection mismatch.");
        if (config.ProjectId != selection.ProjectId || config.Environment != selection.Environment ||
            config.Channel != selection.Channel || config.TargetPlatform != selection.Platform) throw new InvalidDataException("Runtime release track mismatch.");
        if(config.IsManagedDeployment)ManagedClientContext.Bind(config,selection);
        else VersionedReleasePaths.Bind(config, selection);
        config.VersionPolicy = "exact"; config.RequestedVersion = selection.Version;
    }

    internal static async Task<Process> LaunchServiceAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        await LaunchPolicy.VerifyOnlineAsync(config, cancellationToken);
        Boundary.Value?.Invoke("authorized");cancellationToken.ThrowIfCancellationRequested();
        RuntimeServiceState.RequireLaunch(config, true);
        var ticket = RuntimeStore.BeginUnderServiceLock(config, RuntimeIdentities.Current(), HostExecutable());
        try{return await StartHostAsync(new(config, ticket, null, config.SelectedRelease), cancellationToken);}
        catch(OperationCanceledException){RuntimeStore.AbortBeforeStart(config,ticket,RuntimeIdentities.Current());throw;}
    }

    private static async Task<Process> StartHostAsync(RuntimeHostSession input, CancellationToken cancellationToken)
    {
        var ticket = input.Ticket;
        Boundary.Value?.Invoke("ticket-created");
        var info = new ProcessStartInfo(ticket.HostExecutable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("runtime-host");
        SanitizeEnvironment(info);
        Boundary.Value?.Invoke("before-host-start");cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(info) ?? throw new InvalidOperationException("Runtime host did not start; launch state requires inspection.");
        // Ticket goes through a private inherited pipe, never the command line or logs.
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(input, JsonFiles.Options).Replace("\r", "").Replace("\n", ""));
        process.StandardInput.Close();
        // Process.Start is the start commitment: cancellation cannot claim the app was never started afterward.
        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (ready is not null)
        {
            using var result = JsonDocument.Parse(ready);
            if (result.RootElement.TryGetProperty("code", out var code) && code.GetString() == "runtime-data-unavailable") throw new RuntimeDataException();
        }
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
