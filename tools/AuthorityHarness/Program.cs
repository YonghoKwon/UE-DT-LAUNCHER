using System.Text.Json;
using System.Text.Json.Nodes;
using UeDtLauncher.Distribution.Authority;

if (args.Length < 2) return 2;

try
{
    if (args[0] == "crash-init")
        AuthorityDisk.Boundary.Value = PauseAt(Enum.Parse<AuthorityBoundary>(args[2]));
    var store = AuthorityStore.Open(args[1]);
    if (store.OpenMaintenanceError is { } error) throw error;
    switch (args[0])
    {
        case "probe":
            Console.WriteLine(store.ReadSnapshot().Generation);
            break;
        case "reopen":
            var snapshot = store.ReadSnapshot();
            var events = store.ReadAudit(snapshot).ToArray();
            var projected = File.ReadLines(store.DataPath("audit.jsonl"))
                .Select(line => JsonSerializer.Deserialize<StoredAuthorityEvent>(line, AuthorityDocuments.Json)).ToArray();
            if (!events.SequenceEqual(projected!)) throw new InvalidDataException("Reopened audit projection differs from authority.");
            Print(new { snapshot.Generation, areas = AuthorityDocuments.Areas.ToDictionary(AuthorityDocuments.Name, snapshot.Get), events });
            break;
        case "writer":
            Console.WriteLine("READY");
            if (Console.ReadLine() != "START") return 2;
            for (var index = 0; index < 500; index++)
            {
                var subject = args[2] + "/" + index;
                var result = store.Commit(current =>
                {
                    var catalog = current.Get(AuthorityArea.Catalog);
                    var counter = catalog.TryGetProperty("counter", out var value) ? value.GetInt32() : 0;
                    return new(new Dictionary<AuthorityArea, JsonElement> { [AuthorityArea.Catalog] = Counter(catalog, counter + 1) },
                        [new(DateTimeOffset.UnixEpoch, "increment", subject)]);
                });
                if (result.MaintenanceError is { } failed) throw failed;
                Console.WriteLine(result.Generation);
            }
            break;
        case "crash":
            var boundary = Enum.Parse<AuthorityBoundary>(args[2]);
            var kind = args[3];
            // Inspect the event staging path for the mixed commit, after its two data areas.
            var remaining = kind == "mixed" && boundary is AuthorityBoundary.GenerationWrite or AuthorityBoundary.GenerationFlush
                or AuthorityBoundary.GenerationRename or AuthorityBoundary.GenerationDirectoryFlush ? 3 : 1;
            AuthorityDisk.Boundary.Value = PauseAt(boundary, remaining);
            store.Commit(current => MakeCrashPlan(current, kind));
            throw new InvalidOperationException("The requested boundary was not reached.");
        case "crash-init":
            throw new InvalidOperationException("The requested initialization boundary was not reached.");
        default:
            return 2;
    }
    return 0;
}
catch (AuthorityRootException error)
{
    Console.Error.WriteLine($"{error.Kind}: {error.Message}");
    return 3;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, AuthorityDocuments.Json));

static Action<AuthorityBoundary> PauseAt(AuthorityBoundary boundary, int remaining = 1) => reached =>
{
    if (reached != boundary || --remaining != 0) return;
    Console.WriteLine("BOUNDARY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite); // Parent kills this owned process at the confirmed boundary.
};

static JsonElement Counter(JsonElement area, int value)
{
    var document = JsonNode.Parse(area.GetRawText())!.AsObject();
    document["counter"] = value;
    return JsonSerializer.SerializeToElement(document);
}

static CommitPlan MakeCrashPlan(AuthoritySnapshot snapshot, string kind)
{
    var changed = new Dictionary<AuthorityArea, JsonElement>();
    if (kind == "mixed")
    {
        changed.Add(AuthorityArea.Catalog, Counter(snapshot.Get(AuthorityArea.Catalog), 42));
        changed.Add(AuthorityArea.Jobs, Counter(snapshot.Get(AuthorityArea.Jobs), 42));
    }
    if (kind == "area") changed.Add(AuthorityArea.Sequence,
        JsonSerializer.SerializeToElement(new { schemaVersion = 1, highWater = 42 }));
    return new(changed, kind == "area" ? [] : [new(DateTimeOffset.UnixEpoch, "crash-commit", kind)]);
}
