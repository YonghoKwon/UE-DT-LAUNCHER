using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    internal static async Task<int> UploadAsync(string[] args, TextWriter? output = null, TextWriter? errors = null, IUploadProcessRunner? runner = null)
    {
        output ??= Console.Out;
        errors ??= Console.Error;
        var format = Get(args, "--format") ?? "text";
        if (format is not ("text" or "json")) throw new CliArgumentException("--format은 text 또는 json을 지정하세요.");
        int? port = null;
        if (Get(args, "--port") is { } portText)
        {
            if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 65535)
                throw new CliArgumentException("--port는 1~65535 정수를 지정하세요.");
            port = parsed;
        }
        if (args.FirstOrDefault() == "init")
        {
            await UploadProfile.InitializeAsync(Required(args, "--output"), Has(args, "--force"));
            output.WriteLine("업로드 프로필을 만들었습니다. 서버/계정·원격 경로·포트·identity 경로를 실제 값으로 편집하세요. 비밀번호·키 내용은 저장하지 않습니다.");
            return 0;
        }
        var profile = Get(args, "--profile") is { } path ? await JsonFiles.ReadAsync<UploadProfile>(path) : new();
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
        var result = await new ReleaseUploader(runner ?? new OpenSshProcessRunner()).ExecuteAsync(Required(args, "--zip"), metadata,
            profile, Get(args, "--upload-id"), Path.Combine(Environment.CurrentDirectory, "upload-work"), Has(args, "--dry-run"),
            progress: value => UploadPresentation.Progress(errors, value));
        if (format == "json") output.WriteLine(JsonSerializer.Serialize(result, JsonFiles.Options));
        else UploadPresentation.Write(output, result);
        return result.Success ? 0 : 1;
    }
}
