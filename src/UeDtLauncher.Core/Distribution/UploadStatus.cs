using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeDtLauncher;

public sealed record UploadStatusRelease(string ProjectId, string Version, string Platform, string Environment, string Channel)
{
    [JsonIgnore]
    public ReleaseSelection Selection => new(ProjectId, Environment, Channel, Platform, Version);
}

public sealed record UploadStatus(string UploadId, string State, DateTimeOffset UpdatedAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UploadStatusRelease? Release = null)
{
    public const int MaximumBytes = 64 * 1024;
    public int SchemaVersion => 1;
    public string Label => State switch
    {
        "transferring" => "전송 중",
        "waiting" => "접수 대기",
        "processing" => "검사 중",
        "failed" => "검사 실패",
        "rejected" => "거절",
        "pending" => "승인 대기",
        "publishing" => "게시 처리 중",
        "published" => "게시됨",
        "recommended" => "현재 추천판",
        _ => throw InvalidResponse()
    };
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason => State switch
    {
        "failed" => "서버의 패키지 검사에 실패했습니다. 운영자에게 검사 결과 확인을 요청하세요.",
        "rejected" => "운영자가 업로드를 거절했습니다. 운영자에게 확인하세요.",
        _ => null
    };

    public static UploadStatus Parse(ReadOnlySpan<byte> bytes, string expectedUploadId, out JsonElement document)
    {
        if (bytes.Length > MaximumBytes) throw InvalidResponse();
        document = JsonSerializer.Deserialize<JsonElement>(bytes);
        CheckProperties(document, ["schemaVersion", "uploadId", "state", "label", "updatedAtUtc"], ["reason", "release"]);
        var version = document.GetProperty("schemaVersion");
        var timestamp = document.GetProperty("updatedAtUtc");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1 ||
            ReadString(document, "uploadId") != expectedUploadId || timestamp.ValueKind != JsonValueKind.String ||
            !timestamp.TryGetDateTimeOffset(out var time) || time.Offset != TimeSpan.Zero ||
            !(timestamp.GetString()!.EndsWith('Z') || timestamp.GetString()!.EndsWith("+00:00", StringComparison.Ordinal)))
            throw InvalidResponse();

        UploadStatusRelease? release = null;
        if (document.TryGetProperty("release", out var tuple) && tuple.ValueKind != JsonValueKind.Null)
        {
            CheckProperties(tuple, ["projectId", "version", "platform", "environment", "channel"], []);
            release = new(ReadString(tuple, "projectId"), ReadString(tuple, "version"), ReadString(tuple, "platform"),
                ReadString(tuple, "environment"), ReadString(tuple, "channel"));
            release.Selection.Validate();
        }
        var status = new UploadStatus(expectedUploadId, ReadString(document, "state"), time, release);
        var reason = document.TryGetProperty("reason", out var value) && value.ValueKind != JsonValueKind.Null
            ? ReadString(document, "reason") : null;
        if (status.Label != ReadString(document, "label") || status.Reason != reason)
            throw InvalidResponse();
        return status;
    }

    private static void CheckProperties(JsonElement element, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!seen.Add(field.Name) || (!required.Contains(field.Name) && !optional.Contains(field.Name)))
                throw InvalidResponse();
        if (!required.All(seen.Contains)) throw InvalidResponse();
    }

    private static string ReadString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidResponse();
    }

    private static InvalidDataException InvalidResponse() => new("Invalid upload status document.");
}
