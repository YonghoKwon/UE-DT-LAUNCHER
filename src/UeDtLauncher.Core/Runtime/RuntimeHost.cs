using System.Text.Json;

namespace UeDtLauncher;

public static class RuntimeHost
{
    public static async Task<int> RunSessionAsync()
    {
        try
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null || line.Length > 128 * 1024) throw new InvalidDataException("Invalid runtime input.");
            var session = JsonSerializer.Deserialize<RuntimeHostSession>(line, JsonFiles.Options) ?? throw new InvalidDataException("Missing runtime session.");
            session.Config.SelectedRelease = session.Selection;
            NativeProcessFamily.PrepareHost();
            var identity = RuntimeIdentities.Current();
            RuntimeHostRequest launch;
            async Task<ManagedAgentResponse> Send(string command, int? pid = null)
            {
                // Restart/busy is retried without abandoning supervision or killing payloads.
                while (true)
                {
                    try
                    {
                        var result = await new ManagedAgentClient(session.AgentEndpoint).SendRuntimeAsync(command, session.Config, session.Ticket, pid);
                        if (result.Success) return result;
                        if (result.Status != "busy") throw new InvalidOperationException("Runtime registration/report was rejected.");
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                    await Task.Delay(500);
                }
            }
            if (session.AgentEndpoint is not null)
                launch = (await Send("launch-attach")).RuntimeLaunch ?? throw new InvalidDataException("Missing authorized launch.");
            else launch = RuntimeStore.Attach(session.Config, session.Ticket, identity, Environment.ProcessPath!);
            launch = RuntimeDataPolicy.PrepareHost(launch, identity);
            var result = NativeProcessFamily.Run(launch, pid =>
            {
                if (session.AgentEndpoint is not null) Send("launch-started", pid).GetAwaiter().GetResult();
                else RuntimeStore.Report(session.Config, session.Ticket, identity, false, pid);
                Emit(new { state="started", pid });
            });
            if (session.AgentEndpoint is not null) await Send("launch-complete");
            else RuntimeStore.Report(session.Config, session.Ticket, identity, true);
            Emit(new { state="completed", result }); return 0;
        }
        catch (Exception ex)
        {
            Emit(new { state="unknown", error=ex.GetType().Name, code=LauncherFailure.Code(ex) }); return 1;
        }
    }
    private static void Emit(object value)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(value)); }
        catch (IOException) { /* The GUI/terminal may have closed; supervision must continue. */ }
    }
    // Deliberately before GUI, self-update, crash reporters or managed Process initialization.
    public static int RunProbe()
    {
        try
        {
            var line = Console.ReadLine();
            if (line is null || line.Length > 64 * 1024) throw new InvalidDataException("Invalid host input.");
            var request = JsonSerializer.Deserialize<RuntimeHostRequest>(line, JsonFiles.Options) ?? throw new InvalidDataException("Missing host input.");
            var result = NativeProcessFamily.Run(request, pid => Emit(new { state="started", pid }));
            Emit(new { state="completed", result }); return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { state="unknown", error=ex.GetType().Name, message=ex.Message })); return 1;
        }
    }
}
