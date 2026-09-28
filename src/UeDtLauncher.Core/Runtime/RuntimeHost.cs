using System.Text.Json;

namespace UeDtLauncher;

public static class RuntimeHost
{
    // Deliberately before GUI, self-update, crash reporters or managed Process initialization.
    public static int RunProbe()
    {
        try
        {
            var line = Console.ReadLine();
            if (line is null || line.Length > 64 * 1024) throw new InvalidDataException("Invalid host input.");
            var request = JsonSerializer.Deserialize<RuntimeHostRequest>(line, JsonFiles.Options) ?? throw new InvalidDataException("Missing host input.");
            var result = NativeProcessFamily.Run(request, pid => Console.WriteLine(JsonSerializer.Serialize(new { state="started", pid })));
            Console.WriteLine(JsonSerializer.Serialize(new { state="completed", result })); return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { state="unknown", error=ex.GetType().Name, message=ex.Message })); return 1;
        }
    }
}
