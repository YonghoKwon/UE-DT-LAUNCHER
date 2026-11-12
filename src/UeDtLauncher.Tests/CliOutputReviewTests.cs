using Xunit;

namespace UeDtLauncher.Tests;

[CollectionDefinition("CLI output review", DisableParallelization = true)]
public sealed class CliOutputReviewCollection;

[Collection("CLI output review")]
public sealed class CliOutputReviewTests
{
    [Fact]
    public async Task UnknownCommandDoesNotEchoArbitraryInput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Assert.Equal(1, await Program.MainAsync(["secret-marker/unknown-command"]));
            Assert.DoesNotContain("secret-marker", error.ToString());
        }
        finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
    }

    [Fact]
    public async Task InvalidBaseUrlWarningDoesNotEchoCredentialsOrPaths()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            await Program.MainAsync(["generate-manifest", "--package-dir", "missing-review-package", "--base-url",
                "ftp://secret-marker:password@host/path", "--entry-point", "app.exe"]);
            Assert.DoesNotContain("secret-marker", error.ToString());
        }
        finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
    }
}
