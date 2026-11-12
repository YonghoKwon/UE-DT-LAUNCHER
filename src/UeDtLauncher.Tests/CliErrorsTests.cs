using Xunit;

namespace UeDtLauncher.Tests;

public class CliErrorsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorsNeverPublishExceptionMessagesOrProfile(bool details)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var error = new IOException("token=secret-marker " + profile,
            new UnauthorizedAccessException("private-inner-marker"));
        using var output = new StringWriter();
        var id = CliErrors.Write(output, error, details);
        var text = output.ToString();
        Assert.Contains("[storage-failed]", text);
        Assert.Contains("지원 ID: " + id, text);
        Assert.Contains("다음 조치:", text);
        Assert.DoesNotContain("secret-marker", text);
        Assert.DoesNotContain("private-inner-marker", text);
        if (!string.IsNullOrEmpty(profile)) Assert.DoesNotContain(profile, text);
        Assert.Equal(details, text.Contains("IOException -> UnauthorizedAccessException"));
        Assert.DoesNotContain(" at ", text);
    }

    [Fact]
    public async Task CliFailureRetainsExitOne()
    {
        Assert.Equal(1, await Program.MainAsync(["run", "--config", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing")]));
    }

    [Fact]
    public void CrashRedactionIncludesInnerMessages()
    {
        var error = new IOException("token=secret-marker", new Exception("password=inner-marker"));
        var text = DiagnosticRedactor.Redact(error.ToString());
        Assert.DoesNotContain("secret-marker", text);
        Assert.DoesNotContain("inner-marker", text);
    }

    [Fact]
    public void ServerCauseIsUsefulWithoutPublishingInput()
    {
        using var output = new StringWriter();
        CliErrors.Write(output, new InvalidOperationException("private revision path"), server: true);
        Assert.Contains("revision", output.ToString());
        Assert.DoesNotContain("private revision path", output.ToString());
    }
}
