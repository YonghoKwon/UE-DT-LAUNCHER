using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UeDtLauncher.Distribution.Authority;

internal enum AuthorityArea { Catalog, Jobs, Auth, Sequence }

internal sealed record AuthorityEvent(DateTimeOffset At, string Action, string Job);

internal sealed record StoredAuthorityEvent(
    [property: JsonRequired] DateTimeOffset At,
    [property: JsonRequired] string Action,
    [property: JsonRequired] string Job,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] Guid TxId);

internal sealed record CommitPlan(
    IReadOnlyDictionary<AuthorityArea, JsonElement> Areas,
    IReadOnlyList<AuthorityEvent> Events)
{
    internal static CommitPlan Audit(params AuthorityEvent[] events) => new(
        new Dictionary<AuthorityArea, JsonElement>(), events);
}

internal sealed record AuthorityCommitResult(long Generation, bool Changed, Exception? MaintenanceError);

internal sealed class AuthorityStoreException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed class AuthorityRootException(AuthorityRootKind kind, string message, Exception? inner = null)
    : IOException(message + " Configure a new empty distribution root or inspect the existing root offline.", inner)
{
    internal AuthorityRootKind Kind { get; } = kind;
}

internal sealed class AuthorityFileSystemException(string message, Exception? inner = null) : IOException(message, inner);

internal sealed class AuthorityCommitDurabilityException(long generation, Exception inner)
    : IOException($"HEAD published generation {generation}, but its directory flush failed. Reopen the store before retrying.", inner)
{
    internal long PublishedGeneration { get; } = generation;
}

internal sealed record AuthorityHead(
    [property: JsonRequired] int Schema,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Files,
    [property: JsonRequired] IReadOnlyDictionary<string, string> Sha256,
    [property: JsonRequired] long? Previous,
    [property: JsonRequired] string HeadSha256);

internal sealed record AuthorityEventBatch(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] StoredAuthorityEvent[] Events,
    [property: JsonRequired] string BatchSha256);

internal sealed class AuthoritySnapshot(
    AuthorityStore store,
    AuthorityHead head,
    IReadOnlyDictionary<AuthorityArea, JsonElement> areas)
{
    internal AuthorityStore Store { get; } = store;
    internal AuthorityHead Head { get; } = head;
    internal long Generation => Head.Generation;
    internal JsonElement Get(AuthorityArea area) => areas[area];
}

internal static class AuthorityDocuments
{
    internal const int Schema = 1;
    internal const string Events = "events";
    internal static IReadOnlyList<AuthorityArea> Areas { get; } = Array.AsReadOnly(Enum.GetValues<AuthorityArea>());
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string Name(AuthorityArea area) => area switch
    {
        AuthorityArea.Catalog => "catalog",
        AuthorityArea.Jobs => "jobs",
        AuthorityArea.Auth => "auth",
        AuthorityArea.Sequence => "sequence",
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    internal static string RelativeFile(string area, long generation) =>
        area + "/" + generation.ToString("D6", CultureInfo.InvariantCulture) + ".json";

    internal static bool TryGeneration(string area, string relativePath, out long generation)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        return long.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out generation) &&
            relativePath == RelativeFile(area, generation);
    }

    internal static JsonElement Empty(AuthorityArea area) => area switch
    {
        AuthorityArea.Catalog => JsonSerializer.SerializeToElement(new
        { schemaVersion = Schema, releases = Array.Empty<object>(), promotions = Array.Empty<object>(), promotionReady = true }, Json),
        AuthorityArea.Jobs => JsonSerializer.SerializeToElement(new
        { schemaVersion = Schema, jobs = Array.Empty<object>(), activeWork = Array.Empty<object>(), maintenanceDeletions = Array.Empty<object>() }, Json),
        AuthorityArea.Auth => JsonSerializer.SerializeToElement(new
        { schemaVersion = Schema, tokens = Array.Empty<object>(), deviceKeys = Array.Empty<object>() }, Json),
        AuthorityArea.Sequence => JsonSerializer.SerializeToElement(new { schemaVersion = Schema, highWater = 0L }, Json),
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json)
        ?? throw new InvalidDataException("Authority document is null.");

    internal static byte[] Encode<T>(T document) => JsonSerializer.SerializeToUtf8Bytes(document, Json);

    internal static AuthorityHead Seal(AuthorityHead head) => head with
    {
        HeadSha256 = Hash(Encode(head with { HeadSha256 = "" }))
    };

    internal static AuthorityHead Freeze(AuthorityHead head) => head with
    {
        Files = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(head.Files)),
        Sha256 = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(head.Sha256))
    };

    internal static void Validate(AuthorityHead head, AuthorityLayout layout)
    {
        if (head.Schema != Schema || head.Generation < 0 ||
            head.Previous != (head.Generation == 0 ? null : head.Generation - 1) ||
            head.Files?.Count != Areas.Count + 1 || head.Sha256?.Count != Areas.Count + 1 ||
            head.HeadSha256 != Seal(head).HeadSha256)
            throw new InvalidDataException("Authority HEAD format or checksum is invalid.");

        foreach (var name in Areas.Select(Name).Append(Events))
        {
            if (!head.Files.TryGetValue(name, out var file) || file is null ||
                !TryGeneration(layout.DirectoryFor(name), file, out var generation) ||
                generation > head.Generation || (name == Events && generation != head.Generation) ||
                !head.Sha256.TryGetValue(name, out var sha) || sha is null || sha.Length != 64 ||
                sha.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Authority HEAD contains an invalid generation reference.");
        }
    }

    internal static JsonElement ValidateArea(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Authority area must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException("Authority area has a duplicate field.");
        if (!document.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != Schema)
            throw new InvalidDataException("Authority area schema is unsupported.");
        return document;
    }

    internal static AuthorityEventBatch MakeBatch(long generation, IReadOnlyList<AuthorityEvent> events)
    {
        var txId = Guid.NewGuid();
        var batch = new AuthorityEventBatch(Schema, generation, events.Select((entry, ordinal) =>
            new StoredAuthorityEvent(entry.At, entry.Action, entry.Job, generation, ordinal, txId)).ToArray(), "");
        return ValidateBatch(batch with { BatchSha256 = Hash(Encode(batch)) }, generation);
    }

    internal static AuthorityEventBatch ValidateBatch(AuthorityEventBatch batch, long generation)
    {
        if (batch.SchemaVersion != Schema || batch.Generation != generation || batch.Events is null ||
            (generation == 0 && batch.Events.Length != 0) ||
            batch.BatchSha256 != Hash(Encode(batch with { BatchSha256 = "" })))
            throw new InvalidDataException("Authority event batch format is invalid.");
        for (var ordinal = 0; ordinal < batch.Events.Length; ordinal++)
        {
            var entry = batch.Events[ordinal];
            if (entry is null || entry.Generation != generation || entry.Ordinal != ordinal ||
                entry.TxId == Guid.Empty || entry.TxId != batch.Events[0].TxId ||
                entry.At == default || string.IsNullOrWhiteSpace(entry.Action) || entry.Job is null)
                throw new InvalidDataException("Authority event format or position is invalid.");
        }
        return batch;
    }
}
