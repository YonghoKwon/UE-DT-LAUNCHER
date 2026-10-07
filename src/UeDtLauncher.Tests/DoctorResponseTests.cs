using System.IO.Pipes;
using System.Net.Sockets;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class DoctorResponseTests
{
    private static readonly DoctorTarget Target = new("managed-agent", "demo", "prod", "stable", "windows-x64", "latest", null);
    private static DoctorReport Passed() => new("now", true, "1", "test",
        [new("config", true, "OK") { State = "passed", Code = "config", Subject = "agent" }])
    { Target = Target, SupportId = "original-support", PreparationState = "checks-passed" };

    [Theory]
    [InlineData("target")]
    [InlineData("preparation")]
    [InlineData("state")]
    [InlineData("unknown-state")]
    public void IncompleteEvidenceSurvivesTransportAndMerge(string missing)
    {
        var report = Passed();
        report = missing switch
        {
            "target" => report with { Target = null },
            "preparation" => report with { PreparationState = null },
            _ => report with { Checks = [report.Checks[0] with { State = missing == "state" ? null : "future-status" }] }
        };
        var request = new ManagedAgentRequest { DoctorTarget = Target };
        var transport = DoctorResponse.ValidateEnvelope(new() { CorrelationId = request.CorrelationId, Success = report.Healthy, DoctorReport = report }, request);
        var combined = DoctorResponse.Merge(transport, Target, [new("display-config", true, "OK") { State = "passed" }]);
        Assert.Equal("verification-pending", combined.PreparationState);
        Assert.Equal(report.Target, combined.Target);
        Assert.Equal("original-support", combined.SupportId);
        Assert.Contains(combined.Checks, c => c.State == "deferred");
    }

    [Theory]
    [InlineData("target")]
    [InlineData("null-list")]
    [InlineData("null-item")]
    [InlineData("healthy-conflict")]
    [InlineData("success-conflict")]
    [InlineData("preparation-conflict")]
    public void MalformedOrContradictoryEvidenceIsSafeError(string kind)
    {
        var report = Passed();
        report = kind switch
        {
            "target" => report with { Target = Target with { ProjectId = "other" } },
            "null-list" => report with { Checks = null! },
            "null-item" => report with { Checks = [null!] },
            "healthy-conflict" => report with { Checks = [new("config", false, "secret-path") { State = "failed" }] },
            "success-conflict" => report with { Checks = [new("config", false, "secret-path") { State = "passed" }] },
            _ => report with { PreparationState = "action-required" }
        };
        var ex = Assert.Throws<AgentOperationException>(() => DoctorResponse.Merge(report, Target, []));
        Assert.Equal("diagnostic-response-invalid", ex.ErrorCode);
        Assert.Equal("original-support", ex.CorrelationId);
        Assert.DoesNotContain("secret-path", ex.Message);
    }

    [Fact]
    public void KnownConfigurationFailureAndNormalResponseArePreserved()
    {
        var failed = Passed() with { Healthy = false, Target = null, PreparationState = "action-required",
            Checks = [new("config", false, "설정 확인") { State = "failed", Code = "configuration-invalid" }] };
        var result = DoctorResponse.Merge(failed, Target, [new("display", true, "OK") { State = "passed" }]);
        Assert.False(result.Healthy); Assert.Null(result.Target); Assert.Equal("action-required", result.PreparationState);
        Assert.Equal("configuration-invalid", result.Checks[1].Code); Assert.Equal("original-support", result.SupportId);
        var normal = DoctorResponse.Validate(Passed(), Target);
        Assert.True(normal.Healthy); Assert.Equal("checks-passed", normal.PreparationState);
        Assert.Equal(Target, normal.Target); Assert.Equal(Passed().Checks, normal.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealIpcLegacyStatusAndIncompleteDoctorResponse(bool capability)
    {
        var endpoint = OperatingSystem.IsWindows() ? "uedt-doctor-" + Guid.NewGuid().ToString("N") :
            Path.Combine(Path.GetTempPath(), "ud-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = OperatingSystem.IsWindows() ? null : new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (listener is not null) { listener.Bind(new UnixDomainSocketEndPoint(endpoint)); listener.Listen(2); }
        var commands = new List<string>();
        async Task ServeAsync()
        {
            for (var i = 0; i < (capability ? 2 : 1); i++)
            {
                await using Stream stream = OperatingSystem.IsWindows() ?
                    new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous) :
                    new NetworkStream(await listener!.AcceptAsync(deadline.Token), ownsSocket: true);
                if (stream is NamedPipeServerStream pipe) await pipe.WaitForConnectionAsync(deadline.Token);
                var request = await ManagedAgentFrameCodec.ReadAsync<ManagedAgentRequest>(stream, deadline.Token);
                commands.Add(request.Command);
                var response = new ManagedAgentResponse { CorrelationId = request.CorrelationId, Success = true,
                    AgentCapabilities = capability ? [DoctorPresentation.Capability] : [],
                    DoctorReport = request.Command == "doctor" ? Passed() with { Target = null } : null };
                await ManagedAgentFrameCodec.WriteAsync(stream, response, deadline.Token);
            }
        }
        var server = ServeAsync();
        try
        {
            if (capability)
            {
                var report = await new ManagedAgentClient(endpoint).DoctorAsync(false, deadline.Token, Target);
                var result = DoctorResponse.Merge(report, Target, [new("client", true, "OK") { State = "passed" }]);
                Assert.Equal("verification-pending", result.PreparationState); Assert.Null(result.Target);
            }
            else
            {
                var error = await Assert.ThrowsAsync<AgentOperationException>(() => new ManagedAgentClient(endpoint).DoctorAsync(false, deadline.Token, Target));
                Assert.Equal("client-upgrade-required", error.ErrorCode);
            }
            await server;
            Assert.Equal(capability ? new[] { "status", "doctor" } : new[] { "status" }, commands);
        }
        finally { deadline.Cancel(); listener?.Dispose(); if (!OperatingSystem.IsWindows()) File.Delete(endpoint); }
    }
}
