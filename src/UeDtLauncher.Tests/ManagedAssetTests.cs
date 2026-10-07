using System.Security.Cryptography;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ManagedAssetTests
{
    [Fact]
    public async Task VerifiedImageStreamPublishesOnlyTheCompletedCacheFile()
    {
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var root = Path.Combine(Path.GetTempPath(), "uedt-image-" + Guid.NewGuid().ToString("N"));
        using var stream = new MemoryStream();
        for (var offset = 0; offset < data.Length; offset += 64 * 1024)
            await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse { CorrelationId = "id", IsFinal = false, Success = true,
                AssetChunk = new(offset, data.Length, hash, ".png", data.Skip(offset).Take(64 * 1024).ToArray()) });
        await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse { CorrelationId = "id", Success = true });
        stream.Position = 0;
        try
        {
            var path = await ManagedAssetReceiver.ReceiveAsync(stream, "id", root, default);
            Assert.Equal(data, File.ReadAllBytes(path!)); Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("hash")]
    [InlineData("incomplete")]
    [InlineData("correlation")]
    [InlineData("extension")]
    [InlineData("oversized")]
    public async Task InvalidImageStreamsLeaveNoCacheFile(string corruption)
    {
        byte[] data = [1, 2, 3]; var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var root = Path.Combine(Path.GetTempPath(), "uedt-image-" + Guid.NewGuid().ToString("N"));
        using var stream = new MemoryStream();
        await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse
        {
            CorrelationId = corruption == "correlation" ? "other" : "id", IsFinal = false, Success = true,
            AssetChunk = new(corruption == "offset" ? 1 : 0, corruption == "oversized" ? 21 * 1024 * 1024 : corruption == "incomplete" ? 4 : 3,
                corruption == "hash" ? new string('0', 64) : hash, corruption == "extension" ? "/../../file" : ".png", data)
        });
        await ManagedAgentFrameCodec.WriteAsync(stream, new ManagedAgentResponse { CorrelationId = "id", Success = true }); stream.Position = 0;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ManagedAssetReceiver.ReceiveAsync(stream, "id", root, default));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ImageCommandCannotRequestUrlsPathsOrReleaseOverrides()
    {
        var request = new ManagedAgentRequest { Command = "project-asset", ProjectId = "demo", AssetKind = "hero", StreamProgress = true };
        Assert.Null(ManagedAgentProtocol.Validate(request));
        request.AssetKind = "http://internal"; Assert.NotNull(ManagedAgentProtocol.Validate(request));
        request.AssetKind = "hero"; request.Selection = new("demo", "prod", "stable", "windows-x64", "1"); Assert.NotNull(ManagedAgentProtocol.Validate(request));
    }
}
