using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    private static async Task<int> UploadAsync(string[] args)
    {
        if (args.FirstOrDefault() == "init")
        {
            await UploadProfile.InitializeAsync(Required(args, "--output"), Has(args, "--force"));
            Console.WriteLine("Upload profile created. Configure user@host, incoming path and optional identity path; no password or private key contents are stored.");
            return 0;
        }
        var profile = Get(args, "--profile") is { } path ? await JsonFiles.ReadAsync<UploadProfile>(path) : new();
        int? port = Get(args, "--port") is { } portText ? int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture) : null;
        profile = profile.Override(new(Get(args, "--server"), Get(args, "--remote-incoming"), port, Get(args, "--identity-file")));
        var metadata = new ReleaseSidecar
        {
            ProjectId = Required(args, "--project-id"),
            DisplayName = Get(args, "--display-name") ?? Required(args, "--project-id"),
            Version = Required(args, "--version"),
            Platform = Required(args, "--platform"),
            EntryPoint = Required(args, "--entry-point"),
            PayloadRoot = Get(args, "--payload-root") ?? ".",
            Environment = Get(args, "--environment") ?? "prod",
            Channel = Get(args, "--channel") ?? "stable",
            ExecutablePaths = (Get(args, "--executable-paths") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Notes = Get(args, "--notes"),
            HeroPath = Get(args, "--hero-path"),
            ThumbnailPath = Get(args, "--thumbnail-path")
        };
        var result = await new ReleaseUploader(new OpenSshProcessRunner()).ExecuteAsync(Required(args, "--zip"), metadata,
            profile, Get(args, "--upload-id"), Path.Combine(Environment.CurrentDirectory, "upload-work"), Has(args, "--dry-run"));
        Console.WriteLine(JsonSerializer.Serialize(result, JsonFiles.Options));
        return result.Success ? 0 : 1;
    }
}
