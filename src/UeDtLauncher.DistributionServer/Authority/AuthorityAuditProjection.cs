using System.Buffers;
using System.Security.Cryptography;

namespace UeDtLauncher.Distribution.Authority;

internal sealed class AuthorityAuditProjection(AuthorityStore store)
{
    private Checkpoint? checkpoint;

    internal void Update(AuthoritySnapshot snapshot)
    {
        AuthorityDisk.Reach(AuthorityBoundary.AuditAppend);
        var path = store.DataPath("audit.jsonl");
        if (Append(path, snapshot)) return;
        Rebuild(path, snapshot);
    }

    private bool Append(string path, AuthoritySnapshot snapshot)
    {
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TrimTornTail(file);
        var end = file.Length;
        file.Position = 0;

        long first = 0;
        if (checkpoint is { } saved)
        {
            if (saved.Generation > snapshot.Generation || saved.Bytes > end ||
                !CheckPrefix(file, hash, saved))
                return false;
            if (saved.Generation == snapshot.Generation && file.Position != end) return false;
            first = saved.Generation == snapshot.Generation ? saved.Generation : saved.Generation + 1;
        }

        if (checkpoint?.Generation != snapshot.Generation)
        {
            for (var generation = first; ; generation++)
            {
                foreach (var entry in store.ReadBatch(snapshot, generation).Events)
                {
                    var expected = EncodeLine(entry);
                    if (file.Position < end)
                    {
                        if (end - file.Position < expected.Length) return false;
                        var existing = new byte[expected.Length];
                        file.ReadExactly(existing);
                        if (!expected.AsSpan().SequenceEqual(existing)) return false;
                    }
                    else file.Write(expected);
                    hash.AppendData(expected);
                }
                if (generation == snapshot.Generation) break;
            }
        }
        if (file.Position < end) return false;
        file.Flush(true);
        checkpoint = new(snapshot.Generation, file.Position, hash.GetHashAndReset());
        return true;
    }

    private static bool CheckPrefix(FileStream file, IncrementalHash hash, Checkpoint saved)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (var remaining = saved.Bytes; remaining > 0;)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                file.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            return saved.Hash.AsSpan().SequenceEqual(hash.GetCurrentHash());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void Rebuild(string path, AuthoritySnapshot snapshot)
    {
        var temporary = AuthorityDisk.Temporary(path);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var entry in store.ReadAudit(snapshot))
                {
                    var line = EncodeLine(entry);
                    file.Write(line);
                    hash.AppendData(line);
                }
                file.Flush(true);
                length = file.Position;
            }
            AuthorityDisk.Rename(temporary, path, replace: true);
            AuthorityDisk.FlushDirectory(store.DirectoryPath);
            checkpoint = new(snapshot.Generation, length, hash.GetHashAndReset());
        }
        finally { AuthorityDisk.TryDelete(temporary); }
    }

    private static byte[] EncodeLine(StoredAuthorityEvent entry)
    {
        var bytes = AuthorityDocuments.Encode(entry);
        Array.Resize(ref bytes, bytes.Length + 1);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private static void TrimTornTail(FileStream file)
    {
        Span<byte> buffer = stackalloc byte[4096];
        for (var end = file.Length; end > 0;)
        {
            var count = (int)Math.Min(end, buffer.Length);
            file.Position = end - count;
            var block = buffer[..count];
            file.ReadExactly(block);
            var lastNewline = block.LastIndexOf((byte)'\n');
            if (lastNewline >= 0)
            {
                var validEnd = end - count + lastNewline + 1;
                if (file.Length != validEnd) file.SetLength(validEnd);
                return;
            }
            end -= count;
        }
        if (file.Length > 0) file.SetLength(0);
    }

    private sealed record Checkpoint(long Generation, long Bytes, byte[] Hash);
}
