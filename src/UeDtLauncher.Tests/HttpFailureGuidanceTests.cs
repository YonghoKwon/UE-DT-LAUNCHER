using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class HttpFailureGuidanceTests
{
    [Theory]
    [InlineData(401, "authentication-failed")]
    [InlineData(403, "access-denied")]
    [InlineData(429, "rate-limited")]
    public void StatusUsesSharedCodeAndGuidance(int status, string expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        var error = Assert.ThrowsAny<HttpRequestException>(() => HttpFailures.EnsureSuccess(response));
        Assert.Equal(expected, LauncherFailure.Code(error));
        Assert.Equal(expected, DoctorPresentation.Failure("server", error, "portable").Code);
        Assert.Equal(expected, LauncherUiError.From(error).Code);
        Assert.NotEqual(LauncherGuidance.For("unknown"), LauncherGuidance.For(error));
    }

    [Fact]
    public void RetryAfterSurvivesManagedResponseAndIsNumericOnly()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(37));
        var error = Assert.Throws<HttpStatusFailureException>(() => HttpFailures.EnsureSuccess(response));
        var managed = new AgentOperationException(error.Code, "support", "safe", HttpFailures.RetryAfter(error));
        Assert.Equal(37, managed.RetryAfterSeconds);
        Assert.Contains("37", LauncherUiError.From(managed).Message);
        Assert.Contains("37", DoctorPresentation.Failure("server", error, "portable").NextAction);
        using var output = new StringWriter();
        CliErrors.Write(output, error);
        Assert.Contains("37", output.ToString());
    }

    [Fact]
    public async Task RepeatedStaleRenewsOnlyOnceAndHasIndependentGuidance()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = LauncherConfigurationTemplates.Distribution(new(ServerUrl: "http://127.0.0.1:18500", DeploymentMode: "portable"));
        var transport = new StaleTransport();
        using var http = new HttpClient(new DeviceSignatureHandler(config, transport, new(1, "pc", key.ExportPkcs8PrivateKeyPem())));
        using var response = await http.GetAsync("http://127.0.0.1:18500/api/v1/catalog");
        var error = Assert.Throws<HttpStatusFailureException>(() => HttpFailures.EnsureSuccess(response));
        Assert.Equal(2, transport.Requests);
        Assert.Equal(2, transport.Challenges);
        Assert.Equal("stale-challenge", LauncherFailure.Code(error));
        Assert.NotEqual(LauncherGuidance.For("authentication-failed"), LauncherGuidance.For(error));
        Assert.Equal("integrity-failed", LauncherFailure.Code(new CryptographicException()));
    }

    private sealed class StaleTransport : HttpMessageHandler
    {
        public int Requests;
        public int Challenges;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/challenge"))
            {
                Challenges++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AuthChallenge("opaque")) });
            }
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
            response.Headers.Add("X-UE-DT-Auth-Error", "stale-challenge");
            return Task.FromResult(response);
        }
    }
}
