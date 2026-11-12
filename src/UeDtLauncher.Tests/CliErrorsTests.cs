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

    [Theory]
    [InlineData("Authorization: Basic secret-marker")]
    [InlineData("authorization=Digest username=secret-marker")]
    [InlineData("password=\"prefix\\\"secret-marker\"")]
    [InlineData("password='prefix\\'secret-marker'")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nsecret-marker\n-----END RSA PRIVATE KEY-----")]
    [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nsecret-marker\n-----END ENCRYPTED PRIVATE KEY-----")]
    public void CrashAndFileLogsRedactCompleteSecretValues(string message)
    {
        var error = new IOException("safe outer", new Exception(message));
        Assert.DoesNotContain("secret-marker", DiagnosticRedactor.Redact(error.ToString()));
        var root = Path.Combine(Path.GetTempPath(), "review-redaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new FileLogger(root);
            logger.Log("failure", error.ToString());
            Assert.DoesNotContain("secret-marker", File.ReadAllText(logger.CurrentLogPath));
            Assert.DoesNotContain("secret-marker", File.ReadAllText(logger.CurrentJsonLogPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StructuredRedactionPreservesJsonEnvelopeAndNestedStringBoundaries()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "failed", count = 429,
            message = "Authorization: Digest username=secret-marker, response=second-marker",
            nested = new[] { "Signature: proof-marker", "safe message" },
            challenge = "challenge-marker"
        });
        var redacted = DiagnosticRedactor.Redact(json);
        Assert.DoesNotContain("\n", redacted);
        Assert.DoesNotContain("\r", redacted);
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(429, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("safe message", document.RootElement.GetProperty("nested")[1].GetString());
        Assert.DoesNotContain("marker", redacted);
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
