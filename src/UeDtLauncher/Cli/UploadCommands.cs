using System.Text.Json;

namespace UeDtLauncher;

public static partial class Program
{
    internal static async Task<int> UploadAsync(string[] args, TextWriter? output = null, TextWriter? errors = null,
        IUploadProcessRunner? runner = null, string? workRoot = null)
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
            output.WriteLine("업로드 프로필을 만들었습니다. 서버/계정·원격 경로·포트·identity 경로를 실제 값으로 편집하세요. 비밀번호·키 내용은 저장하지 않습니다. 키 인증 전용(BatchMode)으로 비밀번호를 묻지 않습니다. upload check로 먼저 점검하세요.");
            return 0;
        }
        if (args.FirstOrDefault() == "project-init")
        {
            var destination = Get(args, "--output") ?? throw new CliArgumentException("--output에 프로젝트 프로필 경로를 지정하세요.");
            await UploadProjectProfile.InitializeAsync(destination, Has(args, "--force"));
            output.WriteLine("프로젝트 프로필을 만들었습니다. projectId, platform, entryPoint와 프로젝트에 필요한 항목을 편집하세요.");
            return 0;
        }

        var serverProfilePath = Get(args, "--profile");
        var serverOptions = new UploadProfile(Get(args, "--server"), Get(args, "--remote-incoming"), port, Get(args, "--identity-file"));
        if (args.FirstOrDefault() == "check")
        {
            var checkProfile = serverProfilePath is null ? new UploadProfile() : await JsonFiles.ReadAsync<UploadProfile>(serverProfilePath);
            var check = await new UploadPreflight(runner ?? new OpenSshProcessRunner()).CheckAsync(checkProfile.Override(serverOptions));
            if (format == "json") output.WriteLine(JsonSerializer.Serialize(check, JsonFiles.Options));
            else UploadPreflight.Write(output, check);
            return check.Success ? 0 : 1;
        }
        var input = await ReleaseMetadataInput.ReadAsync(args);
        var zip = ReleaseMetadataInput.RequireZip(Get(args, "--zip"));
        var dryRun = Has(args, "--dry-run");
        var planWithoutDestination = dryRun && Get(args, "--project-profile") is not null &&
                                     serverProfilePath is null && serverOptions == new UploadProfile();
        var profile = serverProfilePath is null ? new UploadProfile() : await JsonFiles.ReadAsync<UploadProfile>(serverProfilePath);
        profile = planWithoutDestination ? new UploadProfile("upload-user@server.invalid", "/incoming") : profile.Override(serverOptions);
        UploadResult result;
        try
        {
            result = await new ReleaseUploader(runner ?? new OpenSshProcessRunner()).ExecuteAsync(zip, input.Metadata,
                profile, Get(args, "--upload-id"), workRoot ?? Path.Combine(Environment.CurrentDirectory, "upload-work"), dryRun,
                progress: value => UploadPresentation.Progress(errors, value), keepWork: Has(args, "--keep-work"));
        }
        catch (ReleaseDocumentSizeException)
        {
            throw new CliArgumentException("release.json exceeds 1 MiB.");
        }
        if (format == "json")
        {
            var metadata = input.Metadata;
            var json = JsonSerializer.SerializeToNode(result, JsonFiles.Options)!.AsObject();
            json["project"] = JsonSerializer.SerializeToNode(new
            {
                metadata.ProjectId, metadata.Version, metadata.Platform, metadata.Channel, input.NotesBytes
            }, JsonFiles.Options);
            json["sources"] = JsonSerializer.SerializeToNode(input.Sources, JsonFiles.Options);
            if (result.SidecarSha256 is not null) json["sidecarSha256"] = result.SidecarSha256;
            if (planWithoutDestination) json["placeholderDestination"] = true;
            output.WriteLine(json.ToJsonString(JsonFiles.Options));
        }
        else
        {
            UploadPresentation.Write(output, result);
            input.WriteSummary(output);
            if (planWithoutDestination)
                output.WriteLine("계획에 예시 목적지를 사용했습니다. 전송에는 실제 업로드 접속 프로필이나 서버 옵션이 필요합니다.");
        }
        return result.Success ? 0 : 1;
    }
}
