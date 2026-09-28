using System.Text.Json;
using UeDtLauncher;
using UeDtLauncher.Distribution;

var arguments = args.ToList();
if (arguments.Contains("--help"))
{
    Console.WriteLine("UeDtLauncher.DistributionServer <serve|watch|ingest directory|list|inspect id|approve id|reject id|retry id|token-issue client|token-revoke client|usage|cleanup [--apply]> --config server.json");
    Console.WriteLine("client-key add --client id --public-key device-public.json | client-key list | client-key revoke --key-id id; append --config server.json");
    return;
}
var configIndex = arguments.IndexOf("--config");
var configPath = configIndex >= 0 ? arguments[configIndex + 1] : "/etc/ue-dt-distribution/server.json";
if (configIndex >= 0) arguments.RemoveRange(configIndex, 2);
var settings = await JsonFiles.ReadAsync<DistributionSettings>(configPath);
var store = new IntakeStore(settings);
var action = arguments.FirstOrDefault() ?? "list";
string RequiredOption(string name)
{
    var index = arguments.IndexOf(name);
    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : throw new ArgumentException("Missing " + name);
}
switch (action)
{
    case "client-key":
        var keys = new DistributionDeviceKeys(store);
        switch (arguments.ElementAtOrDefault(1))
        {
            case "add":
                var publicPath = RequiredOption("--public-key");
                if (new FileInfo(publicPath).Length > 8192) throw new InvalidDataException("Public registration file exceeds limit.");
                keys.Add(RequiredOption("--client"), await JsonFiles.ReadAsync<DevicePublicKey>(publicPath));
                Console.WriteLine("Device public key registered. Existing access policy remains unchanged."); break;
            case "list": Console.WriteLine(JsonSerializer.Serialize(keys.List().Select(k => new { k.KeyId, k.ClientId, k.Revoked }), JsonFiles.Options)); break;
            case "revoke": keys.Revoke(RequiredOption("--key-id")); Console.WriteLine("Device key revoked."); break;
            default: throw new ArgumentException("client-key requires add, list or revoke.");
        }
        break;
    case "serve": await DistributionHttp.RunAsync(store); break;
    case "usage": Console.WriteLine(JsonSerializer.Serialize(StorageMaintenance.Usage(store), JsonFiles.Options)); break;
    case "cleanup": Console.WriteLine(JsonSerializer.Serialize(StorageMaintenance.Cleanup(store, arguments.Contains("--apply")), JsonFiles.Options)); break;
    case "token-issue": Console.WriteLine(new DistributionTokens(store).Issue(arguments[1])); break;
    case "token-revoke": new DistributionTokens(store).Revoke(arguments[1]); break;
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
