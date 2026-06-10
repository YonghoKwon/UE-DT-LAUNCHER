using Xunit;

namespace UeDtLauncher.Tests;

public class HashingTests
{
    [Fact]
    public async Task Sha256FileAsync_MatchesKnownVector()
    {
        var path = Path.Combine(Path.GetTempPath(), "uedt-hash-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            var hash = await Hashing.Sha256FileAsync(path);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sha256MatchesAsync_IsCaseInsensitive()
    {
        var path = Path.Combine(Path.GetTempPath(), "uedt-hash-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "abc");
            Assert.True(await Hashing.Sha256MatchesAsync(path, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
            Assert.False(await Hashing.Sha256MatchesAsync(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Sha256MatchesAsync_MissingFile_ReturnsFalse()
    {
        Assert.False(await Hashing.Sha256MatchesAsync(Path.Combine(Path.GetTempPath(), "uedt-missing-" + Guid.NewGuid().ToString("N")), new string('a', 64)));
    }
}
