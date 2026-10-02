using System.Text.Json;
using UeDtLauncher;
using UeDtLauncher.Distribution;

var arguments = args.ToList();
if (arguments.Contains("--help"))
{
    Console.WriteLine("UeDtLauncher.DistributionServer <serve|watch|ingest directory|list|inspect id|approve id|promote|promotion inspect|promotion migrate --dry-run|--apply|reject id|retry id|token-issue client|token-revoke client|usage|cleanup [--apply]> --config server.json");
    Console.WriteLine("promote --project-id id --environment prod --channel stable --platform windows-x64 --version version --expected-revision N --reason text; promotion inspect uses the same track flags without version/revision/reason.");
    Console.WriteLine("client-key add --client id --public-key device-public.json | client-key list | client-key revoke --key-id id; append --config server.json");
    Console.WriteLine("token-issue client [--expires-at UTC/offset-ISO] | token-list | token-revoke-id --id management-id; client-key add also accepts --expires-at. No expiry is invented for existing credentials.");
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
var store = new IntakeStore(settings);
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
