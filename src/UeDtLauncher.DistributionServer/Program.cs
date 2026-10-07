using System.Text.Json;
using UeDtLauncher;
using UeDtLauncher.Distribution;

try
{
var arguments = args.ToList();
if (arguments.Contains("--help"))
{
    Console.WriteLine("UeDtLauncher.DistributionServer <serve|watch|ingest directory|list|inspect id|approve id|promote|promotion inspect|promotion migrate --dry-run|--apply|reject id|retry id|token-issue client|token-revoke client|usage|cleanup [--apply]> --config server.json");
    Console.WriteLine("promote --project-id id --environment prod --channel stable --platform windows-x64 --version version --expected-revision N --reason text; promotion inspect uses the same track flags without version/revision/reason.");
    Console.WriteLine("client-key add --client id --public-key device-public.json | client-key list | client-key revoke --key-id id; append --config server.json");
    Console.WriteLine("token-issue client [--expires-at UTC/offset-ISO] | token-list | token-revoke-id --id management-id; client-key add also accepts --expires-at. No expiry is invented for existing credentials.");
    Console.WriteLine("backup plan/create --output new-dir/verify --backup dir; restore plan/stage --backup dir --target empty-dir/activate --target dir --confirm");
    Console.WriteLine("retention inspect/plan --jobs failed-id or --temporary processing/dir --output plan.json/apply --plan plan.json --confirm; stop serve/watch before apply.");
    return;
}
var configIndex = arguments.IndexOf("--config");
var configPath = configIndex >= 0 ? arguments[configIndex + 1] : "/etc/ue-dt-distribution/server.json";
if (configIndex >= 0) arguments.RemoveRange(configIndex, 2);
var settings = await JsonFiles.ReadAsync<DistributionSettings>(configPath);
var action = arguments.FirstOrDefault() ?? "list";
string RequiredOption(string name)
{
    var index = arguments.IndexOf(name);
    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : throw new ArgumentException("Missing " + name);
}
DateTimeOffset? Expiry()
{
    if (!arguments.Contains("--expires-at")) return null;
    var text = RequiredOption("--expires-at");
    if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var value) ||
        (!text.EndsWith('Z') && !System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d\d:\d\d$")))
        throw new ArgumentException("--expires-at requires an explicit UTC/offset ISO timestamp.");
    return value;
}
if (action == "promotion" && arguments.ElementAtOrDefault(1) == "migrate")
{
    if (arguments.Contains("--dry-run") == arguments.Contains("--apply")) throw new ArgumentException("Choose --dry-run or --apply.");
    if (arguments.Contains("--dry-run")) { Console.WriteLine(JsonSerializer.Serialize(ReleasePromotions.PreviewMigration(settings), JsonFiles.Options)); return; }
}
if (action is "backup" or "restore")
{
    var command = arguments.ElementAtOrDefault(1);
    object result = (action, command) switch
    {
        ("backup", "plan") => DistributionBackup.Plan(settings),
        ("backup", "create") => await DistributionBackup.CreateAsync(settings, RequiredOption("--output")),
        ("backup", "verify") => await DistributionBackup.VerifyAsync(RequiredOption("--backup")),
        ("restore", "plan") => new { Requirement = "New empty target, same origin/signer, surviving latest source, stopped processes, explicit activation", Public = false },
        ("restore", "stage") => await DistributionBackup.StageAsync(RequiredOption("--backup"), RequiredOption("--target"), settings),
        ("restore", "activate") => await DistributionBackup.ActivateAsync(RequiredOption("--target"), settings, arguments.Contains("--confirm")),
        _ => throw new ArgumentException("Use backup plan/create/verify or restore plan/stage/activate.")
    };
    Console.WriteLine(JsonSerializer.Serialize(result, JsonFiles.Options)); return;
}
if(action=="retention")
{
    string[] Values(string option) => arguments.Contains(option) ? RequiredOption(option).Split(',',StringSplitOptions.RemoveEmptyEntries) : [];
    object result=arguments.ElementAtOrDefault(1) switch
    {
        "inspect"=>RetentionMaintenance.Inspect(settings),
        "plan"=>await RetentionMaintenance.PlanAsync(settings,Values("--jobs"),Values("--temporary")),
        "apply"=>await RetentionMaintenance.ApplyAsync(settings,await JsonFiles.ReadAsync<RetentionPlan>(RequiredOption("--plan")),arguments.Contains("--confirm")),
        _=>throw new ArgumentException("Use retention inspect/plan/apply.")
    };
    if(arguments.Contains("--output"))await JsonFiles.WriteAsync(RequiredOption("--output"),result);
    Console.WriteLine(JsonSerializer.Serialize(result,JsonFiles.Options));return;
}
if(action=="cleanup" && arguments.Contains("--apply"))throw new InvalidOperationException("Use retention plan followed by retention apply --plan ... --confirm; legacy unconfirmed cleanup apply is disabled.");
var store = new IntakeStore(settings);
using var operationLease = DistributionMaintenanceLease.Acquire(settings.Root, false);
ReleaseSelection Selection(bool version) => new(RequiredOption("--project-id"), RequiredOption("--environment"), RequiredOption("--channel"), RequiredOption("--platform"), version ? RequiredOption("--version") : "inspect");
switch (action)
{
    case "promote":
        if (!long.TryParse(RequiredOption("--expected-revision"), out var revision)) throw new ArgumentException("Invalid expected revision.");
        Console.WriteLine(JsonSerializer.Serialize(new ReleasePromotions(store).Promote(Selection(true), revision, RequiredOption("--reason")), JsonFiles.Options)); break;
    case "promotion":
        var promotion = new ReleasePromotions(store);
        Console.WriteLine(JsonSerializer.Serialize(arguments.ElementAtOrDefault(1) switch
        {
            "inspect" => (object)promotion.Inspect(Selection(false)),
            "migrate" => promotion.Migrate(),
            _ => throw new ArgumentException("promotion requires inspect or migrate.")
        }, JsonFiles.Options)); break;
    case "client-key":
        var keys = new DistributionDeviceKeys(store);
        switch (arguments.ElementAtOrDefault(1))
        {
            case "add":
                var publicPath = RequiredOption("--public-key");
                if (new FileInfo(publicPath).Length > 8192) throw new InvalidDataException("Public registration file exceeds limit.");
                keys.Add(RequiredOption("--client"), await JsonFiles.ReadAsync<DevicePublicKey>(publicPath), Expiry());
                Console.WriteLine("Device public key registered. Existing access policy remains unchanged."); break;
            case "list": Console.WriteLine(JsonSerializer.Serialize(keys.List().Select(k => new { k.KeyId, k.ClientId, k.Revoked, k.ExpiresAtUtc, k.Expired }), JsonFiles.Options)); break;
            case "revoke": keys.Revoke(RequiredOption("--key-id")); Console.WriteLine("Device key revoked."); break;
            default: throw new ArgumentException("client-key requires add, list or revoke.");
        }
        break;
    case "serve": await DistributionHttp.RunAsync(store); break;
    case "usage": Console.WriteLine(JsonSerializer.Serialize(StorageMaintenance.Usage(store), JsonFiles.Options)); break;
    case "cleanup": Console.WriteLine(JsonSerializer.Serialize(StorageMaintenance.Cleanup(store, arguments.Contains("--apply")), JsonFiles.Options)); break;
    case "token-issue": Console.WriteLine(new DistributionTokens(store).Issue(arguments[1], Expiry())); break;
    case "token-revoke": new DistributionTokens(store).Revoke(arguments[1]); break;
    case "token-list": Console.WriteLine(JsonSerializer.Serialize(new DistributionTokens(store).List(), JsonFiles.Options)); break;
    case "token-revoke-id": new DistributionTokens(store).RevokeId(RequiredOption("--id")); break;
    case "approve": Console.WriteLine(JsonSerializer.Serialize(await new ApprovedPublisher(store).ApproveAsync(arguments[1]), JsonFiles.Options)); break;
    case "ingest": Console.WriteLine(JsonSerializer.Serialize(await store.IngestAsync(arguments[1]), JsonFiles.Options)); break;
    case "list": Console.WriteLine(JsonSerializer.Serialize(store.List(), JsonFiles.Options)); break;
    case "inspect": Console.WriteLine(JsonSerializer.Serialize(store.Get(arguments[1]), JsonFiles.Options)); break;
    case "reject": store.Reject(arguments[1]); break;
    case "retry": store.Retry(arguments[1]); break;
    case "watch":
        using (var cancel = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            try { while (!cancel.IsCancellationRequested) { await store.ScanAsync(cancel.Token); await Task.Delay(2000, cancel.Token); } }
            catch (OperationCanceledException) { }
        }
        break;
    default: throw new ArgumentException("Use ingest, list, inspect, reject, retry, or watch.");
}
}
catch(Exception error)
{
    Console.Error.WriteLine("Distribution operation rejected: " + error.GetType().Name + ". Check configuration, ownership and offline maintenance conditions.");
    Environment.ExitCode=1;
}
