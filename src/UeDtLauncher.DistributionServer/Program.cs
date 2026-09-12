using System.Text.Json;
using UeDtLauncher;
using UeDtLauncher.Distribution;

var arguments = args.ToList();
var configIndex = arguments.IndexOf("--config");
var configPath = configIndex >= 0 ? arguments[configIndex + 1] : "/etc/ue-dt-distribution/server.json";
if (configIndex >= 0) arguments.RemoveRange(configIndex, 2);
var settings = await JsonFiles.ReadAsync<DistributionSettings>(configPath);
var store = new IntakeStore(settings);
var action = arguments.FirstOrDefault() ?? "list";
switch (action)
{
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
