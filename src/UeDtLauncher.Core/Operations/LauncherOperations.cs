using System.Collections.Concurrent;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record OperationStatus(int SchemaVersion, string Id, string Owner, string Session,
    ReleaseSelection? Selection, string Phase, bool CancellationRequested, string UpdatedAtUtc,
    string? ManifestSha256 = null, string Command = "update");

/// <summary>Local authenticated ownership, independent of the lifetime of an IPC connection.</summary>
public sealed class OperationRegistry(string root)
{
    public const string Capability = "cancellable-operations-v1";
    private readonly object gate = new();
    private readonly Dictionary<string, OperationHandle> active = new(StringComparer.Ordinal);
    private string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid operation ID.");
        return SafePath.ResolveInsideChecked(root, id + ".json");
    }
    private static void Authorize(OperationStatus record, RuntimeIdentity peer)
    {
        if (!peer.Administrator && (record.Owner != peer.Owner || record.Session != peer.Session))
            throw new UnauthorizedAccessException("Operation belongs to another OS user/session.");
    }
    internal void Save(OperationStatus record)
    {
        var path = PathFor(record.Id); Directory.CreateDirectory(root);
        using var writer = AcquireWriter(path + ".write.lock");
        if (File.Exists(path))
        {
            var prior = JsonFiles.ReadAsync<OperationStatus>(path).GetAwaiter().GetResult();
            if (prior.CancellationRequested) record = record with { CancellationRequested = true };
        }
        JsonFiles.WriteAsync(path, record).GetAwaiter().GetResult();
    }
    private static FileStream AcquireWriter(string path)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5)) { Thread.Sleep(10); }
        }
    }
    private static bool IsOwned(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    public OperationHandle Begin(string id, RuntimeIdentity peer, ReleaseSelection? selection, CancellationToken shutdown = default, string command = "update")
    {
        lock (gate)
        {
            var path = PathFor(id);
            if (File.Exists(path) || active.Count >= 8) throw new InvalidOperationException("Operation already exists or capacity reached.");
            if (Directory.Exists(root) && Directory.EnumerateFiles(root, "*.json").Take(4096).Count() >= 4096)
                throw new IOException("Operation record limit reached; review and discard old records.");
            selection?.Validate();
            if (command is not ("update" or "repair")) throw new ArgumentException("Unsupported operation kind.");
            Directory.CreateDirectory(root);
            var ownership = new FileStream(path + ".active.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (File.Exists(path)) throw new InvalidOperationException("Operation already exists.");
                var record = new OperationStatus(1, id, peer.Owner, peer.Session, selection, "Pending", false, DateTimeOffset.UtcNow.ToString("O"), Command: command);
                Save(record);
                var handle = new OperationHandle(this, record, shutdown, ownership); active.Add(id, handle); return handle;
            }
            catch { ownership.Dispose(); throw; }
        }
    }
    public OperationStatus Inspect(string id, RuntimeIdentity peer)
    {
        lock (gate)
        {
            var path = PathFor(id);
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Operation record unavailable.");
            var record = JsonFiles.ReadAsync<OperationStatus>(path).GetAwaiter().GetResult();
            if (record.SchemaVersion != 1 || record.Id != id || string.IsNullOrWhiteSpace(record.Owner) || string.IsNullOrWhiteSpace(record.Session) ||
                record.Phase is not ("Pending" or "Downloading" or "Applying" or "Cancelling" or "Completed" or "Cancelled" or "Failed" or "Discarded"))
                throw new InvalidDataException("Operation record is incomplete.");
            Authorize(record, peer);
            if (!active.ContainsKey(id) && !IsOwned(path + ".active.lock") && record.Phase is "Pending" or "Downloading" or "Applying" or "Cancelling")
                record = record with { Phase = "Interrupted" };
            return record;
        }
    }
    public OperationStatus Cancel(string id, RuntimeIdentity peer)
    {
        lock (gate)
        {
            var current = Inspect(id, peer);
            if (active.TryGetValue(id, out var handle)) handle.RequestCancellation();
            else if (current.Phase is "Pending" or "Downloading" or "Applying" or "Cancelling")
                Save(current with { CancellationRequested = true, Phase = "Cancelling" });
            return Inspect(id, peer);
        }
    }
    public OperationStatus Discard(string id, RuntimeIdentity peer)
    {
        lock (gate)
        {
            var current = Inspect(id, peer);
            if (active.ContainsKey(id) || IsOwned(PathFor(id) + ".active.lock")) throw new InvalidOperationException("Cancel and wait before discarding an active operation.");
            Save(current with { Phase = "Discarded", UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") });
            return Inspect(id, peer);
        }
    }
    internal void End(string id) { lock (gate) active.Remove(id); }
}

public sealed class OperationHandle : IDisposable
{
    private readonly OperationRegistry registry;
    private readonly CancellationTokenSource cancellation;
    private readonly object gate = new();
    private readonly FileStream ownership;
    private readonly CancellationTokenSource monitorStop = new();
    private readonly Task monitor;
    public OperationStatus Status { get; private set; }
    public CancellationToken Token => cancellation.Token;
    internal OperationHandle(OperationRegistry registry, OperationStatus status, CancellationToken shutdown, FileStream ownership)
    {
        this.registry = registry; Status = status; this.ownership = ownership;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        monitor = Task.Run(async () =>
        {
            try
            {
                while (!monitorStop.IsCancellationRequested)
                {
                    await Task.Delay(100, monitorStop.Token);
                    if (registry.Inspect(status.Id, new(1, "monitor", "", status.Owner, status.Session, false)).CancellationRequested)
                        cancellation.Cancel();
                }
            }
            catch (OperationCanceledException) { }
            catch { cancellation.Cancel(); } // An unreadable operation record cannot authorize continuing changes.
        });
    }
    public void Bind(ReleaseSelection? selection, string? manifestSha256 = null)
    {
        lock (gate)
        {
            if (Status.Selection is not null && Status.Selection != selection) throw new InvalidDataException("Operation release changed.");
            if (Status.Selection == selection && (manifestSha256 is null || Status.ManifestSha256 == manifestSha256)) return;
            Update(Status with { Selection = selection, ManifestSha256 = manifestSha256 ?? Status.ManifestSha256 });
        }
    }
    public void Phase(string phase)
    {
        lock (gate)
        {
            if (Status.Phase == phase || Status.Phase == "Cancelling") return;
            Update(Status with { Phase = phase });
        }
    }
    internal void RequestCancellation()
    {
        lock (gate)
        {
            if (Status.Phase is "Completed" or "Cancelled" or "Failed") return;
            Update(Status with { Phase = "Cancelling", CancellationRequested = true });
            cancellation.Cancel();
        }
    }
    public void Finish(bool committed, bool failed = false)
    {
        lock (gate) Update(Status with { Phase = committed ? "Completed" : failed ? "Failed" : cancellation.IsCancellationRequested ? "Cancelled" : "Completed" });
    }
    private void Update(OperationStatus next)
    { next = next with { UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") }; registry.Save(next); Status = next; }
    public void Dispose()
    {
        monitorStop.Cancel(); monitor.GetAwaiter().GetResult();
        registry.End(Status.Id); ownership.Dispose(); cancellation.Dispose(); monitorStop.Dispose();
    }
}
