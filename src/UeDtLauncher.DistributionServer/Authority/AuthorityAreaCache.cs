using System.Text.Json;

namespace UeDtLauncher.Distribution.Authority;

internal sealed class AuthorityAreaCache() : AuthorityParsedCache<JsonElement>(
    bytes => AuthorityDocuments.ValidateArea(AuthorityDocuments.Decode<JsonElement>(bytes)));

internal class AuthorityParsedCache<T>(Func<byte[], T> parse, int entryLimit = 64)
{
    internal const int EntryLimit = 64;
    internal const int ByteLimit = 16 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<(string Path, string Hash), LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> leastRecentFirst = [];
    private int bytes;

    internal T Parse(string path, string hash, byte[] verifiedBytes)
    {
        // The caller has already verified bytes. A warm cache must not hide a missing or changed file.
        lock (gate)
        {
            var key = (path, hash);
            if (entries.TryGetValue(key, out var hit))
            {
                leastRecentFirst.Remove(hit);
                leastRecentFirst.AddLast(hit);
                return hit.Value.Document;
            }
            var document = parse(verifiedBytes);
            if (verifiedBytes.Length > ByteLimit) return document;

            while (entries.Count >= entryLimit || bytes > ByteLimit - verifiedBytes.Length)
            {
                var oldest = leastRecentFirst.First!.Value;
                leastRecentFirst.RemoveFirst();
                entries.Remove(oldest.Key);
                bytes -= oldest.Bytes;
            }
            entries.Add(key, leastRecentFirst.AddLast(new Entry(key, document, verifiedBytes.Length)));
            bytes += verifiedBytes.Length;
            return document;
        }
    }

    private sealed record Entry((string Path, string Hash) Key, T Document, int Bytes);
}
