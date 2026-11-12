using Xunit;

namespace UeDtLauncher.Tests;

public class CliArgumentsTests
{
    [Theory]
    [InlineData("--config")]
    [InlineData("--config", "--repair")]
    [InlineData("--config", "one", "--config", "two")]
    [InlineData("--repair", "--repair")]
    public async Task InvalidSuppliedOptionsFailBeforeIo(params string[] args)
    {
        Assert.Equal(2, await Program.MainAsync(["run", .. args]));
    }

    [Fact]
    public void ValidAndUnknownOptionsKeepLegacyBehavior()
    {
        string[] args = ["--config", "path with spaces.json", "--future-option", "value", "--repair"];
        CliArguments.Validate(args, ["--config"]);
        Assert.Equal("path with spaces.json", CliArguments.Get(args, "--config"));
        Assert.Null(CliArguments.Get(args, "--output"));
    }

    [Fact]
    public void HelpSeparatesCurrentAndCompatibilityCommands()
    {
        using var output = new StringWriter();
        Program.PrintHelp(output);
        var text = output.ToString();
        var current = text.IndexOf("Current commands (distribution server mode)", StringComparison.Ordinal);
        var legacy = text.IndexOf("Compatibility commands (static Manifest/Catalog mode)", StringComparison.Ordinal);
        Assert.True(current >= 0 && legacy > current);
        Assert.True(text.IndexOf("  doctor --config", StringComparison.Ordinal) < legacy);
        foreach (var command in new[] { "generate-manifest", "update-catalog", "list-releases", "generate-nginx-acl", "sign-manifest", "publish-release", "service" })
            Assert.True(text.IndexOf("  " + command + " ", StringComparison.Ordinal) > legacy);
    }
}
