using System.Text.RegularExpressions;

namespace UeDtLauncher;

internal static class ManagedAssetReceiver
{
    internal static async Task<string?> ReceiveAsync(Stream stream, string correlationId, string cacheRoot, CancellationToken token)
    {
        Directory.CreateDirectory(cacheRoot);
        var scratch = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + ".part");
        string? hash = null, extension = null; long total = 0, received = 0;
        try
        {
            await using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                while (true)
                {
                    var frame = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentResponse>(stream, token);
                    if (frame.ProtocolVersion != ManagedAgentProtocol.Version || frame.CorrelationId != correlationId)
                        throw new InvalidDataException("Image IPC response identity mismatch.");
                    if (frame.IsFinal)
                    {
                        if (!frame.Success) return null;
                        if (frame.AssetChunk is not null || hash is null || received != total) throw new InvalidDataException("Incomplete image stream.");
                        break;
                    }
                    var chunk = frame.AssetChunk ?? throw new InvalidDataException("Missing image chunk.");
                    if (!frame.Success || chunk.Data is null || chunk.Data.Length is < 1 or > 64 * 1024 || chunk.TotalBytes is < 1 or > 20 * 1024 * 1024 ||
                        chunk.Offset != received || chunk.Offset + chunk.Data.Length > chunk.TotalBytes ||
                        !Regex.IsMatch(chunk.Sha256, "\\A[0-9a-f]{64}\\z") || chunk.Extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                        throw new InvalidDataException("Invalid image chunk.");
                    if (hash is null) { hash = chunk.Sha256; extension = chunk.Extension; total = chunk.TotalBytes; }
                    if (hash != chunk.Sha256 || extension != chunk.Extension || total != chunk.TotalBytes) throw new InvalidDataException("Image changed mid-transfer.");
                    await output.WriteAsync(chunk.Data, token); received += chunk.Data.Length;
                }
            }
            if (!await Hashing.Sha256MatchesAsync(scratch, hash!, token)) throw new InvalidDataException("Image checksum mismatch.");
            var target = Path.Combine(cacheRoot, hash + extension); File.Move(scratch, target, true); return target;
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }
}
