using System.Text.Json;
using System.Text.Json.Nodes;
using UeDtLauncher;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;

if (args.Length < 2) return 2;

try
{
    if (args[0] is "intake-crash" or "intake-reopen") return RunIntake(args);
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
        case "catalog-approve":
        case "catalog-promote":
        case "catalog-serve":
            return await RunCatalogCommand(store, args);
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

static async Task<int> RunCatalogCommand(AuthorityStore authority, string[] args)
{
    var settings = await JsonFiles.ReadAsync<DistributionSettings>(args[2]);
    var intake = new IntakeStore(settings);
    CatalogAuthority.Bind(intake, authority);
    if (args[0] == "catalog-serve")
    {
        await DistributionHttp.RunAsync(intake);
        return 0;
    }
    if (args[0] == "catalog-promote")
    {
        Console.WriteLine("READY");
        if (Console.ReadLine() != "START") return 2;
        var selection = new ReleaseSelection("demo", "prod", "stable", "windows-x64", args[3]);
        Print(new ReleasePromotions(intake).Promote(selection, long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), "test"));
        return 0;
    }

    if (args.Length >= 5)
    {
        var boundary = Enum.Parse<AuthorityBoundary>(args[4]);
        var occurrence = boundary is AuthorityBoundary.GenerationWrite or AuthorityBoundary.GenerationFlush
            or AuthorityBoundary.GenerationRename or AuthorityBoundary.GenerationDirectoryFlush ? 2 : 1;
        CatalogAuthority.PublicationBoundary.Value = PauseAt(boundary, occurrence);
    }
    Print(await new ApprovedPublisher(intake).ApproveAsync(args[3]));
    return 0;
}

static int RunIntake(string[] args)
{
    var store = new IntakeStore(new() { Root = args[1] });
    var authority = store.Authority;
    if (authority.OpenMaintenanceError is { } error) throw error;
    if (args[0] == "intake-reopen")
    {
        var snapshot = authority.ReadSnapshot();
        var events = authority.ReadAudit(snapshot).ToArray();
        var projected = File.ReadLines(authority.DataPath("audit.jsonl"))
            .Select(line => JsonSerializer.Deserialize<StoredAuthorityEvent>(line, AuthorityDocuments.Json));
        if (!events.SequenceEqual(projected!)) throw new InvalidDataException("Reopened audit projection differs from authority.");
        Print(new { snapshot.Generation, jobs = snapshot.Get(AuthorityArea.Jobs), events });
        return 0;
    }

    var boundary = Enum.Parse<AuthorityBoundary>(args[2]);
    var occurrence = boundary is AuthorityBoundary.GenerationWrite or AuthorityBoundary.GenerationFlush or
        AuthorityBoundary.GenerationRename or AuthorityBoundary.GenerationDirectoryFlush ? 2 : 1;
    AuthorityDisk.Boundary.Value = PauseAt(boundary, occurrence);
    const string id = "fixture";
    using var jobLock = store.LockJob(id);
    switch (args[3])
    {
        case "X1":
            store.Save(new(id, Path.Combine(store.Root, "incoming", id), "pending", null, "updated"));
            break;
        case "X2":
            store.ChangeState(id, "failed", "waiting", reset: true);
            break;
        case "X3":
            _ = new IntakeWorkClaim(store, store.Get(id), "validating", Path.Combine(store.Root, "processing", "scratch"), null, "waiting");
            break;
        default:
            return 2;
    }
    throw new InvalidOperationException("The requested intake boundary was not reached.");
}

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
