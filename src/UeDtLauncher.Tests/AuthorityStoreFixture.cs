using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeDtLauncher.Distribution.Authority;
using Xunit;

namespace UeDtLauncher.Tests;

internal sealed class AuthorityStoreFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("uedt-authority-").FullName;
    internal AuthorityStore Open() => AuthorityStore.Open(Root);

    internal static AuthorityEvent Event(string job, string action = "audit") => new(DateTimeOffset.UnixEpoch, action, job);

    internal static JsonElement Set(JsonElement original, string name, int value)
    {
        var document = JsonNode.Parse(original.GetRawText())!.AsObject();
        document[name] = value;
        return JsonSerializer.SerializeToElement(document);
    }

    internal static CommitPlan SetCounters(AuthoritySnapshot snapshot, int value, params AuthorityEvent[] events) => new(
        new Dictionary<AuthorityArea, JsonElement>
        {
            [AuthorityArea.Catalog] = Set(snapshot.Get(AuthorityArea.Catalog), "counter", value),
            [AuthorityArea.Jobs] = Set(snapshot.Get(AuthorityArea.Jobs), "counter", value)
        }, events);

    internal static StoredAuthorityEvent[] ReadProjection(AuthorityStore store) =>
        File.ReadLines(store.DataPath("audit.jsonl")).Select(line =>
            JsonSerializer.Deserialize<StoredAuthorityEvent>(line, AuthorityDocuments.Json)!).ToArray();

    internal static AuthorityCommitResult Committed(AuthorityCommitResult result)
    {
        Assert.True(result.Changed);
        Assert.Null(result.MaintenanceError);
        return result;
    }

    internal static Child Start(string root, params string[] arguments)
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var repo = testOutput;
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "UeDtLauncher.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var harness = Path.Combine(repo.FullName, "tools", "AuthorityHarness", "bin", testOutput.Parent!.Name, "net8.0",
            "UeDtLauncher.AuthorityHarness.dll");
        Assert.True(File.Exists(harness), $"Missing test harness: {harness}");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(harness);
        start.ArgumentList.Add(arguments[0]);
        start.ArgumentList.Add(root);
        foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        return new(Process.Start(start) ?? throw new IOException("Test harness could not start."));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);

    internal sealed class Child : IDisposable
    {
        internal Process Process { get; }
        private readonly Task<string> errors;

        internal Child(Process process)
        {
            Process = process;
            errors = process.StandardError.ReadToEndAsync();
        }

        internal async Task<string?> ReadLine() =>
            await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

        internal async Task<(int ExitCode, string Output, string Error)> Finish()
        {
            var output = Process.StandardOutput.ReadToEndAsync();
            await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            return (Process.ExitCode, await output, await errors);
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }
            Process.Dispose();
        }
    }
}
