using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using UeDtLauncher.Distribution;
using UeDtLauncher.Distribution.Authority;

internal static class PlatformChecks
{
    private const int Commits = 500;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(8);
    private static readonly string[] Boundaries =
    [
        "GenerationWrite", "GenerationFlush", "GenerationRename", "GenerationDirectoryFlush",
        "HeadWrite", "HeadRename", "AuditAppend"
    ];

    internal static async Task<int?> RunCommand(string[] args)
    {
        var root = args[1];
        switch (args[0])
        {
            case "platform-suite":
                Require(!Path.Exists(root), "The suite needs an unused fixture path.");
                Directory.CreateDirectory(root);
                var final = args.Length == 3 && args[2] == "final";
                var status = await Gate("G2", () => ConcurrentReaders(Path.Combine(root, "readers")));
                if (!final)
                {
                    status |= await Gate("G3", () => Cleanup(Path.Combine(root, "cleanup")));
                    status |= await Gate("G4", () => Leases(Path.Combine(root, "lease")));
                }
                status |= await Gate("G5", () => Crashes(root, final ? 1 : 3));
                return status;
            case "platform-reader":
                await Reader(root);
                return 0;
            case "platform-hold":
                HoldGeneration(root);
                return 0;
            case "platform-lease":
                return HoldLease(root, args[2] == "exclusive");
            case "platform-write":
                Write(root, int.Parse(args[2]), waitForStart: true);
                return 0;
            case "platform-seed":
                Seed(root);
                return 0;
            case "platform-verify":
                Print(VerifyRestart(root, args[2]));
                return 0;
            default:
                return null;
        }
    }

    private static async Task<int> Gate(string id, Func<Task> check)
    {
        try
        {
            await check();
            Print(new { gate = id, status = "PASS" });
            return 0;
        }
        catch (Exception error)
        {
            Print(new { gate = id, status = "FAIL", error = error.ToString() });
            return 1;
        }
    }

    private static async Task ConcurrentReaders(string root)
    {
        Open(root);
        using var writer = Start("platform-write", root, Commits.ToString());
        var readers = new List<Child>();
        try
        {
            await writer.Expect("READY");
            for (var i = 0; i < 4; i++) readers.Add(Start("platform-reader", root));
            foreach (var reader in readers) await reader.Expect("READY");
            foreach (var reader in readers) reader.Send("START");
            foreach (var reader in readers) await reader.Expect("READING");
            writer.Send("START");
            using var written = JsonDocument.Parse(await writer.Success());
            Require(written.RootElement.GetProperty("first").GetInt64() == 1 &&
                written.RootElement.GetProperty("last").GetInt64() == Commits, "Writer did not publish the full range.");
            var failures = new List<string>();
            foreach (var reader in readers)
            {
                try
                {
                    var result = await reader.Success();
                    using var summary = JsonDocument.Parse(result);
                    Require(summary.RootElement.GetProperty("intermediate").GetInt32() > 0,
                        "Reader did not observe an intermediate generation.");
                    Console.WriteLine(result);
                }
                catch (Exception error) { failures.Add(error.Message); }
            }
            Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
            var store = Open(root);
            var snapshot = store.ReadSnapshot();
            ValidateCounters(snapshot);
            Require(snapshot.Generation == Commits, "The final generation differs from the writer's result.");
            var events = ReadProjection(store, snapshot);
            Require(events.Length == Commits && events.Select(entry => (entry.Generation, entry.Ordinal, entry.Job))
                .SequenceEqual(Enumerable.Range(1, Commits).Select(i => ((long)i, 0, $"write/{i}"))),
                "Writer audit contains missing, reordered or duplicate entries.");
            Print(new { commits = snapshot.Generation, readerProcesses = readers.Count, events = events.Length });
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }

    private static async Task Reader(string root)
    {
        var store = Open(root);
        var initial = store.ReadSnapshot();
        Require(initial.Generation == 0, "Reader did not start at generation zero.");
        using var head = AuthorityDisk.OpenReader(store.DataPath("HEAD"));
        using var area = AuthorityDisk.OpenReader(store.DataPath(initial.Head.Files["catalog"]));
        var headHash = StreamHash(head);
        var areaHash = initial.Head.Sha256["catalog"];
        Signal("READY");
        Require(Console.ReadLine() == "START", "Missing reader start instruction.");
        var reads = 0;
        var intermediate = 0;
        var extraHeadReads = 0;
        var headReads = 0;
        long last = 0;
        var clock = Stopwatch.StartNew();
        AuthorityStore.AfterReadHead.Value = _ => headReads++;
        while (true)
        {
            headReads = 0;
            var snapshot = store.ReadSnapshot();
            ValidateCounters(snapshot);
            Require(snapshot.Generation >= last && snapshot.Generation <= Commits, "Reader observed an invalid generation transition.");
            Require(StreamHash(head) == headHash && StreamHash(area) == areaHash, "An open generation changed in place.");
            extraHeadReads += Math.Max(0, headReads - 1);
            reads++;
            last = snapshot.Generation;
            if (last is > 0 and < Commits) intermediate++;
            if (reads == 1) Signal("READING");
            if (last == Commits) break;
            Require(clock.Elapsed < RunTimeout, "Reader timed out before observing the final commit.");
            await Task.Delay(1);
        }
        Print(new { pid = Environment.ProcessId, reads, intermediate, extraHeadReads, last, mixed = 0, finalFailures = 0 });
    }

    private static async Task Cleanup(string root)
    {
        var store = Open(root);
        var old = store.DataPath(store.ReadSnapshot().Head.Files["catalog"]);
        using var holder = Start("platform-hold", root);
        await holder.Expect("READY");
        var last = AuthorityStore.PriorGenerationsToKeep + 1;
        Write(root, last);
        var presentWhileOpen = File.Exists(old);
        holder.Send("READ");
        await holder.Expect("UNCHANGED");
        holder.Send("EXIT");
        await holder.Success();
        var presentAfterClose = File.Exists(old);
        Write(root, 1);
        var presentAfterNextCommit = File.Exists(old);
        Print(new { expiredAt = last, presentWhileOpen, continuedRead = true, presentAfterClose, presentAfterNextCommit });
        Require(presentWhileOpen == OperatingSystem.IsWindows() && presentAfterClose == presentWhileOpen &&
            !presentAfterNextCommit, "Old generation visibility did not follow the platform retention contract.");
    }

    private static void HoldGeneration(string root)
    {
        var store = Open(root);
        var initial = store.ReadSnapshot();
        var path = store.DataPath(initial.Head.Files["catalog"]);
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Signal("READY");
        Require(Console.ReadLine() == "READ", "Missing generation read instruction.");
        Require(StreamHash(handle) == initial.Head.Sha256["catalog"], "Open generation could not be reread.");
        Signal("UNCHANGED");
        Require(Console.ReadLine() == "EXIT", "Missing generation release instruction.");
    }

    private static async Task Leases(string root)
    {
        Open(root);
        using (var exclusive = Start("platform-lease", root, "exclusive"))
        {
            await exclusive.Expect("ACQUIRED");
            await Denied(root, "shared");
            await Denied(root, "exclusive");
            await exclusive.Kill();
        }
        using (var shared1 = Start("platform-lease", root, "shared"))
        using (var shared2 = Start("platform-lease", root, "shared"))
        {
            await shared1.Expect("ACQUIRED");
            await shared2.Expect("ACQUIRED");
            await Denied(root, "exclusive");
            await shared1.Kill();
            await Denied(root, "exclusive");
            await shared2.Kill();
        }
        using var recovered = Start("platform-lease", root, "exclusive");
        await recovered.Expect("ACQUIRED");
        recovered.Send("EXIT");
        await recovered.Success();
        Print(new { exclusiveDeniesShared = true, exclusiveDeniesExclusive = true, simultaneousShared = 2,
            sharedDeniesExclusive = true, ownedKills = 3, reacquiredExclusive = true });
    }

    private static async Task Denied(string root, string mode)
    {
        using var child = Start("platform-lease", root, mode);
        await child.Expect("BUSY");
        Require(await child.Exit() == 4, "Lease rejection exit code differs.");
    }

    private static int HoldLease(string root, bool exclusive)
    {
        DistributionMaintenanceLease lease;
        try { lease = DistributionMaintenanceLease.Acquire(root, exclusive, create: false); }
        catch (IOException error) when (LeaseDenied(error, root))
        {
            Signal("BUSY");
            return 4;
        }
        using (lease)
        {
            Signal("ACQUIRED");
            return Console.ReadLine() == "EXIT" ? 0 : 2;
        }
    }

    private static bool LeaseDenied(IOException error, string root)
    {
        if (OperatingSystem.IsWindows()) return (error.HResult & 0xffff) == 32;
        // .NET's Unix FileStream can encounter the held flock during Open, before
        // DistributionMaintenanceLease calls flock itself.
        var path = Path.Combine(Path.GetFullPath(root), ".maintenance.lock");
        return error.Message == "Server/workers are active or another maintenance operation owns this root." ||
            error.Message == $"The process cannot access the file '{path}' because it is being used by another process.";
    }

    private static async Task Crashes(string root, int repeats)
    {
        var failed = 0;
        foreach (var boundary in Boundaries)
        for (var i = 1; i <= repeats; i++)
        {
            var target = Path.Combine(root, $"crash-{boundary}-{i}");
            try
            {
                Seed(target);
                using (var child = Start("crash", target, boundary, "mixed"))
                {
                    await child.Expect("BOUNDARY");
                    var exit = await child.Kill();
                    Require(exit != 0, "Interrupted writer reported success.");
                }
                using var fresh = Start("platform-verify", target, boundary);
                using var result = JsonDocument.Parse(await fresh.Success());
                Print(new { boundary, attempt = i, status = "PASS", result = result.RootElement });
            }
            catch (Exception error)
            {
                failed++;
                Print(new { boundary, attempt = i, status = "FAIL", error = error.ToString() });
            }
        }
        Require(failed == 0, $"{failed} of {Boundaries.Length * repeats} process crash cases failed.");
    }

    private static void Seed(string root)
    {
        Require(!Path.Exists(root), "Crash fixture path already exists.");
        Write(root, 1);
    }

    private static object VerifyRestart(string root, string boundary)
    {
        var store = Open(root);
        var snapshot = store.ReadSnapshot();
        var published = boundary is "HeadRename" or "AuditAppend";
        var expectedGeneration = published ? 2 : 1;
        Require(snapshot.Generation == expectedGeneration, "Restart observed the wrong generation.");
        foreach (var area in AuthorityDocuments.Areas)
        {
            var expectedArea = AuthorityDocuments.Empty(area);
            if (area is AuthorityArea.Catalog or AuthorityArea.Jobs)
                expectedArea = SetCounter(expectedArea, published ? 42 : 1);
            Require(snapshot.Get(area).GetRawText() == expectedArea.GetRawText(), $"Area {area} differs after restart.");
        }
        var events = ReadProjection(store, snapshot);
        var expected = published ? new[] { (1L, 0, "increment", "write/1"), (2L, 0, "crash-commit", "mixed") }
            : [(1L, 0, "increment", "write/1")];
        Require(events.Select(entry => (entry.Generation, entry.Ordinal, entry.Action, entry.Job)).SequenceEqual(expected) &&
            events.All(entry => entry.At == DateTimeOffset.UnixEpoch) &&
            events.Select(entry => entry.TxId).Distinct().Count() == expectedGeneration, "Restart audit history differs.");
        Require(!Directory.EnumerateFiles(store.DirectoryPath, "*", SearchOption.AllDirectories)
            .Any(file => Path.GetFileName(file).Contains(".tmp-")), "Crash recovery left temporary files.");
        Require(File.Exists(store.DataPath("events/000002.json")) == published, "Crash recovery retained the wrong event batch.");
        Require(store.Commit(_ => CommitPlan.Audit(new AuthorityEvent(DateTimeOffset.UnixEpoch, "audit", "after-reopen")))
            is { MaintenanceError: null, Changed: true } next && next.Generation == expectedGeneration + 1,
            "The first post-recovery commit failed.");
        return new { generation = snapshot.Generation, events = events.Length, duplicate = 0, missing = 0, next = expectedGeneration + 1 };
    }

    private static StoredAuthorityEvent[] ReadProjection(AuthorityStore store, AuthoritySnapshot snapshot)
    {
        var events = store.ReadAudit(snapshot).ToArray();
        var projected = File.ReadLines(store.DataPath("audit.jsonl"))
            .Select(line => JsonSerializer.Deserialize<StoredAuthorityEvent>(line, AuthorityDocuments.Json)).ToArray();
        Require(events.SequenceEqual(projected), "The audit projection differs from authority.");
        return events;
    }

    private static void Write(string root, int count, bool waitForStart = false)
    {
        var store = Open(root);
        if (waitForStart)
        {
            Signal("READY");
            Require(Console.ReadLine() == "START", "Missing writer start instruction.");
        }
        var initial = store.ReadSnapshot().Generation;
        for (var i = 1; i <= count; i++)
        {
            var expected = initial + i;
            var commit = store.Commit(snapshot => new(
                new Dictionary<AuthorityArea, JsonElement>
                {
                    [AuthorityArea.Catalog] = SetCounter(snapshot.Get(AuthorityArea.Catalog), expected),
                    [AuthorityArea.Jobs] = SetCounter(snapshot.Get(AuthorityArea.Jobs), expected)
                }, [new(DateTimeOffset.UnixEpoch, "increment", $"write/{expected}")]));
            if (commit.MaintenanceError is { } error) throw error;
            Require(commit.Changed && commit.Generation == expected, "Writer skipped a generation.");
        }
        if (waitForStart) Print(new { first = initial + 1, last = initial + count });
    }

    private static JsonElement SetCounter(JsonElement area, long counter)
    {
        var document = JsonNode.Parse(area.GetRawText())!.AsObject();
        document["counter"] = counter;
        return JsonSerializer.SerializeToElement(document);
    }

    private static void ValidateCounters(AuthoritySnapshot snapshot)
    {
        foreach (var area in new[] { AuthorityArea.Catalog, AuthorityArea.Jobs })
        {
            var value = snapshot.Get(area);
            var counter = value.TryGetProperty("counter", out var property) ? property.GetInt64() : 0;
            Require(counter == snapshot.Generation, $"{area} counter {counter} differs at generation {snapshot.Generation}.");
        }
    }

    private static AuthorityStore Open(string root)
    {
        var store = AuthorityStore.Open(root);
        if (store.OpenMaintenanceError is { } error) throw error;
        return store;
    }

    private static string StreamHash(Stream handle)
    {
        handle.Position = 0;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(handle)).ToLowerInvariant();
    }

    private static void Print<T>(T data) => Signal(JsonSerializer.Serialize(data, AuthorityDocuments.Json));
    private static void Signal(string line) { Console.WriteLine(line); Console.Out.Flush(); }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static Child Start(params string[] args) => new(args);

    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;

        internal Child(string[] args)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            stderr = process.StandardError.ReadToEndAsync();
        }

        internal async Task Expect(string expected)
        {
            var actual = await process.StandardOutput.ReadLineAsync().WaitAsync(HandshakeTimeout);
            if (actual != expected)
            {
                var error = process.HasExited || actual is null ? await stderr.WaitAsync(HandshakeTimeout) : "";
                throw new InvalidDataException($"PID {process.Id}: expected {expected}, got {actual ?? "EOF"}. {error}");
            }
        }

        internal void Send(string line) { process.StandardInput.WriteLine(line); process.StandardInput.Flush(); }

        internal async Task<int> Exit()
        {
            await process.WaitForExitAsync().WaitAsync(RunTimeout);
            return process.ExitCode;
        }

        internal async Task<string> Success()
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var exit = await Exit();
            Require(exit == 0, $"PID {process.Id} exited {exit}: {await stderr}");
            return (await stdout).Trim();
        }

        internal async Task<int> Kill()
        {
            Require(!process.HasExited, $"Owned PID {process.Id} exited before forced termination.");
            process.Kill();
            return await Exit();
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit(5000);
            }
            process.Dispose();
        }
    }
}
