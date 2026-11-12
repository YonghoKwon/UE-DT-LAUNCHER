using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> RunAgentClientAsync(string[] args)
    {
        var command = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "status";
        var endpoint = Get(args, "--endpoint");
        var projectId = Get(args, "--project");
        var version = Get(args, "--version");
        var selection = version is null ? null : new ReleaseSelection(projectId ?? throw new ArgumentException("--project is required"),
            Get(args, "--environment") ?? "prod", Get(args, "--channel") ?? "stable",
            Get(args, "--platform") ?? (OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64"), version);
        if (command == "project-asset")
        {
            var path = await new ManagedAgentClient(endpoint).GetProjectAssetAsync(projectId ?? throw new ArgumentException("--project is required"), Required(args, "--kind"), Required(args, "--cache"));
            Console.WriteLine(path is null ? "Project image is unavailable." : "Verified project image: " + path);
            return path is null ? 1 : 0;
        }
        var response = await new ManagedAgentClient(endpoint).SendStreamingAsync(command, projectId, _ => { }, selection: selection);
        if (selection is not null && response.Success && response.SelectedRelease != selection)
            throw new InvalidDataException("Agent did not confirm the requested release.");
        PrintAgentResponse(response);
        if (!string.IsNullOrWhiteSpace(response.ClientIdentity)) Console.WriteLine($"Client: {response.ClientIdentity}");
        return response.Success ? 0 : 1;
    }

    internal static void PrintAgentResponse(ManagedAgentResponse response)
    {
        response.ThrowIfFailed();
        foreach (var progress in response.Progress)
            Console.WriteLine(progress.Percent.HasValue ? $"[{progress.Stage}] {progress.Message} ({progress.Percent:0}%)" : $"[{progress.Stage}] {progress.Message}");
        Console.WriteLine($"Agent {response.Status}: {response.Message}");
        Console.WriteLine($"Version: {response.AgentVersion}");
    }

    private static int RunCredentialCommand(string[] args)
    {
        var action = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? "status";
        var name = Get(args, "--name") ?? throw new ArgumentException("credential requires --name <credential-name>.");
        var layout = DeviceCredentials.StorageLayout(Get(args, "--storage") ?? "managed");
        if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var inspection = DeviceCredentials.Inspect(name, layout);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, JsonFiles.Options));
            return inspection.Ready ? 0 : 1;
        }
        if (action == "keygen")
        {
            var output = Path.GetFullPath(Required(args, "--public-out"));
            if (File.Exists(output)) throw new IOException("Public registration file already exists.");
            var key = DeviceCredentials.Generate(name, Required(args, "--key-id"), layout);
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            System.Text.Json.JsonSerializer.Serialize(stream, key, JsonFiles.Options);
            Console.WriteLine($"Device key '{key.KeyId}' stored. Only the public registration file was exported.");
            return 0;
        }
        if (action == "repair-permissions")
        {
            if (args.Contains("--apply") == args.Contains("--dry-run")) throw new ArgumentException("Choose --dry-run or --apply.");
            Console.WriteLine(DeviceCredentials.RepairPermissions(name, args.Contains("--apply"), layout));
            return 0;
        }
        if (action.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            DeviceCredentials.Delete(name, layout);
            Console.WriteLine($"Local credential '{name}' was deleted. Server revocation is a separate administrator operation.");
            return 0;
        }
        if (!action.Equals("set", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown credential action: {action}.");

        var token = Environment.GetEnvironmentVariable("UE_DT_CREDENTIAL_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) token = ReadSecretFromConsole("Bearer token: ");
        CredentialStore.Save(name, token, layout);
        Console.WriteLine($"Credential '{name}' was stored. The token value will not be displayed.");
        return 0;
    }

    private static string ReadSecretFromConsole(string prompt)
    {
        Console.Write(prompt);
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        Console.WriteLine();
        return builder.ToString();
    }

}
