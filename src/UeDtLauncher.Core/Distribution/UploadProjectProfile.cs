using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeDtLauncher;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class UploadProjectProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string? ProjectId { get; init; }
    public string? DisplayName { get; init; }
    public string? Platform { get; init; }
    public string? PayloadRoot { get; init; }
    public string? EntryPoint { get; init; }
    public List<string>? ExecutablePaths { get; init; }
    public string? Environment { get; init; }
    public string? Channel { get; init; }
    public string? HeroPath { get; init; }
    public string? ThumbnailPath { get; init; }

    public static Task InitializeAsync(string output, bool force) =>
        JsonFiles.WriteAsync(output, new UploadProjectProfile(), force);

    public static UploadProjectProfile Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException();

        var profile = document.RootElement.Deserialize<UploadProjectProfile>(JsonFiles.Options)!;
        if (profile.SchemaVersion != 1 || profile.ExecutablePaths?.Any(path => path is null) == true)
            throw new JsonException();
        return profile;
    }
}
