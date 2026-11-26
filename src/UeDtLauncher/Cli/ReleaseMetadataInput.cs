using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

internal sealed record ReleaseMetadataInput(ReleaseSidecar Metadata, IReadOnlyDictionary<string, string> Sources)
{
    private const int MaxNotesBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public int NotesBytes => Encoding.UTF8.GetByteCount(Metadata.Notes ?? "");

    public static async Task<ReleaseMetadataInput> ReadAsync(string[] args)
    {
        var notes = CliArguments.Get(args, "--notes");
        var notesFile = CliArguments.Get(args, "--notes-file");
        if (notes is not null && notesFile is not null)
            throw new CliArgumentException("--notes와 --notes-file 중 하나만 지정하세요.");

        var profilePath = CliArguments.Get(args, "--project-profile");
        var profile = profilePath is null ? new UploadProjectProfile() : await ReadProfileAsync(profilePath);
        var sources = new Dictionary<string, string>();

        string? Pick(string field, string option, string? profileValue, string? defaultValue = null)
        {
            var command = CliArguments.Get(args, option);
            sources[field] = Source(command, profileValue);
            return command ?? profileValue ?? defaultValue;
        }

        string Require(string field, string option, string? profileValue = null)
        {
            var value = Pick(field, option, profileValue);
            if (string.IsNullOrWhiteSpace(value))
                throw new CliArgumentException($"{option}에 필요한 값이 없습니다.");
            return value;
        }

        var projectId = Require("projectId", "--project-id", profile.ProjectId);
        var executablePaths = CliArguments.Get(args, "--executable-paths");
        var metadata = new ReleaseSidecar
        {
            ProjectId = projectId,
            DisplayName = Pick("displayName", "--display-name", profile.DisplayName, projectId)!,
            Version = Require("version", "--version"),
            Platform = Require("platform", "--platform", profile.Platform),
            PayloadRoot = Pick("payloadRoot", "--payload-root", profile.PayloadRoot, ".")!,
            EntryPoint = Require("entryPoint", "--entry-point", profile.EntryPoint),
            ExecutablePaths = executablePaths?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? profile.ExecutablePaths ?? [],
            Environment = Pick("environment", "--environment", profile.Environment, "prod")!,
            Channel = Pick("channel", "--channel", profile.Channel, "stable")!,
            HeroPath = Pick("heroPath", "--hero-path", profile.HeroPath),
            ThumbnailPath = Pick("thumbnailPath", "--thumbnail-path", profile.ThumbnailPath)
        };
        sources["executablePaths"] = Source(executablePaths, profile.ExecutablePaths);
        sources["notes"] = notesFile is null ? Source(notes, null) : "notes-file";
        try
        {
            metadata.ValidateReleaseFields();
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        {
            throw new CliArgumentException("프로젝트 식별자, 버전, 플랫폼, 환경, 채널 또는 ZIP 내부 경로 형식을 확인하세요.");
        }

        metadata.Notes = notesFile is null ? notes : await ReadNotesAsync(notesFile);
        return new(metadata, sources);
    }

    public static string RequireZip(string? zip)
    {
        if (zip is null || !File.Exists(zip))
            throw new CliArgumentException("--zip에 기존 ZIP 파일을 지정하세요.");
        return zip;
    }

    public void WriteSummary(TextWriter output)
    {
        output.WriteLine($"적용 프로젝트: {Metadata.ProjectId} / {Metadata.Version} / {Metadata.Platform} / {Metadata.Channel} / 노트 {NotesBytes} B");
        output.WriteLine("값의 출처: " + string.Join(", ", Sources.Select(source => $"{source.Key}={SourceLabel(source.Value)}")));
    }

    private static string Source(object? command, object? profile) =>
        command is not null ? "command-line" : profile is not null ? "project-profile" : "default";

    private static string SourceLabel(string source) => source switch
    {
        "command-line" => "명령줄",
        "project-profile" => "프로젝트 프로필",
        "notes-file" => "노트 파일",
        _ => "기본값"
    };

    private static async Task<UploadProjectProfile> ReadProfileAsync(string path)
    {
        var bytes = await ReadBoundedAsync(path, "--project-profile", SidecarPackageValidator.MaxDocumentBytes, "1 MiB");
        try
        {
            StrictUtf8.GetCharCount(bytes.Span);
            return UploadProjectProfile.Parse(bytes);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new CliArgumentException("--project-profile의 JSON 형식, schemaVersion 및 허용 필드를 확인하세요.");
        }
    }

    private static async Task<string> ReadNotesAsync(string path)
    {
        var bytes = await ReadBoundedAsync(path, "--notes-file", MaxNotesBytes, "64 KiB");
        try { return StrictUtf8.GetString(bytes.Span); }
        catch (DecoderFallbackException)
        {
            throw new CliArgumentException("--notes-file은 올바른 UTF-8 텍스트여야 합니다.");
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(string path, string option, int limit, string limitLabel)
    {
        var bytes = new byte[limit + 1];
        int count;
        try
        {
            await using var stream = File.OpenRead(path);
            count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CliArgumentException($"{option}에 읽을 수 있는 기존 파일을 지정하세요.");
        }
        if (count > limit) throw new CliArgumentException($"{option}은 {limitLabel} 이하여야 합니다.");
        var offset = bytes.AsSpan(0, count).StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return bytes.AsMemory(offset, count - offset);
    }
}
