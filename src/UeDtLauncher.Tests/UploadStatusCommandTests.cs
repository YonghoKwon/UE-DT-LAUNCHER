using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class UploadStatusCommandTests
{
    private static string[] Args(string id = "upload-1", params string[] options) =>
        ["status", "--upload-id", id, "--server", "upload@host.example", "--remote-incoming", "/srv/ue/incoming", .. options];

    [Fact]
    public async Task UploadCliShowsStatusCommandOnTextSuccess()
    {
        using var temp = new UploadStatusTestDirectory();
        var zip = Path.Combine(temp.Path, "fixture.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var entry = new StreamWriter(archive.CreateEntry("game.exe").Open()))
            await entry.WriteAsync("fixture");

        string[] args = ["--zip", zip, "--project-id", "demo", "--version", "1.0",
            "--platform", "windows-x64", "--entry-point", "game.exe", "--server", "upload@host",
            "--remote-incoming", "/srv/incoming", "--upload-id", "upload-1"];
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await Program.UploadAsync(args, output, errors, new TransferRunner()));
        Assert.Contains("upload status --upload-id upload-1 --wait", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await Program.UploadAsync([.. args, "--dry-run"], output, errors, new TransferRunner()));
        Assert.DoesNotContain("upload status --upload-id", output.ToString());
    }

    [Fact]
    public async Task UsesFixedSshArgumentsAndPreservesValidatedJson()
    {
        var runner = new FakeRunner(Reply("pending"));
        var (exit, output, errors) = await Run(runner, Args(options: ["--format", "json", "--port", "2222"]));
        Assert.Equal(0, exit);
        Assert.Empty(errors);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("ssh", call.Executable);
        Assert.Equal(65536, call.Limit);
        Assert.Equal(new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "UpdateHostKeys=no",
            "-o", "PreferredAuthentications=publickey", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
            "-o", "PermitLocalCommand=no", "-o", "ClearAllForwardings=yes", "-o", "ControlMaster=no", "-o", "ControlPath=none",
            "-o", "ConnectTimeout=10", "-o", "ConnectionAttempts=1", "-p", "2222", "-n", "-T", "upload@host.example",
            ExpectedCommand("/srv/ue/upload-status") }, call.Args);
        using var original = JsonDocument.Parse(Reply("pending").StandardOutput);
        using var observed = JsonDocument.Parse(output);
        foreach (var field in original.RootElement.EnumerateObject())
            Assert.Equal(field.Value.GetRawText(), observed.RootElement.GetProperty(field.Name).GetRawText());
        Assert.Equal(TimeSpan.Zero, observed.RootElement.GetProperty("queriedAtUtc").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task ExplicitStatusPathOverridesProfileAndLegacyProfileUsesSibling()
    {
        using var temp = new UploadStatusTestDirectory();
        var profile = Path.Combine(temp.Path, "upload-profile.json");
        await File.WriteAllTextAsync(profile, """{"server":"u@host","remoteIncoming":"/incoming","port":22}""");
        var runner = new FakeRunner(Reply("pending"), Reply("pending"), Reply("pending"));
        string[] args = ["status", "--upload-id", "upload-1", "--profile", profile];
        Assert.Equal(0, (await Run(runner, args)).Exit);
        await File.WriteAllTextAsync(profile, """{"server":"u@host","remoteStatus":"/read/status","port":22}""");
        Assert.Equal(0, (await Run(runner, args)).Exit);
        Assert.Equal(0, (await Run(runner, [.. args, "--remote-status", "/overridden"])).Exit);
        Assert.Equal(new[] { ExpectedCommand("/upload-status"), ExpectedCommand("/read/status"),
            ExpectedCommand("/overridden") }, runner.Calls.Select(call => call.Args[^1]));
    }

    public static TheoryData<string> UnsafeSegments => new()
    {
        "bad\nname", "bad;name", "bad$(name)", "bad`name", "..", "bad name", "업로드", new string('a', 129)
    };

    [Theory]
    [MemberData(nameof(UnsafeSegments))]
    public async Task InvalidIdsAndUnsafePathsDoNotInvokeSsh(string segment)
    {
        var runner = new FakeRunner();
        Assert.Equal(2, (await Run(runner, Args(segment))).Exit);
        // Remote path syntax has no existing segment length limit. A valid upload ID
        // can be paired with a longer but otherwise valid remote path.
        if (segment.Length <= 128)
        {
            Assert.Equal(2, (await Run(runner, Args(options: ["--remote-status", "/srv/" + segment]))).Exit);
            Assert.Equal(2, (await Run(runner, ["status", "--upload-id", "upload-1", "--server", "u@host",
                "--remote-incoming", "/srv/" + segment])).Exit);
        }
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("pending", 0)]
    [InlineData("publishing", 0)]
    [InlineData("published", 0)]
    [InlineData("recommended", 0)]
    [InlineData("failed", 3)]
    [InlineData("rejected", 3)]
    public async Task WaitStopsAtSpecifiedStates(string state, int expected)
    {
        var runner = new FakeRunner(Reply(state));
        var (exit, output, errors) = await Run(runner, Args(options: ["--wait"]));
        Assert.Equal(expected, exit);
        var response = new UploadStatus("upload-1", state, DateTimeOffset.UnixEpoch);
        Assert.Contains(response.Label, output);
        if (response.Reason is not null) Assert.Contains(response.Reason, output);
        Assert.Empty(errors);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task SingleQueryReturnsZeroForAReportedFailureAndOneForSshFailure()
    {
        Assert.Equal(0, (await Run(new(Reply("failed")), Args())).Exit);
        var (exit, output, errors) = await Run(new(new UploadProcessOutput(255, Encoding.UTF8.GetBytes("private stderr fixture"))), Args());
        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Contains("호스트 키", errors);
        Assert.DoesNotContain("private stderr fixture", errors);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--interval", "999999999")]
    [InlineData("--format", "yaml")]
    public async Task InvalidOptionReturnsTwoWithoutCallingSsh(string option, string value)
    {
        var runner = new FakeRunner();
        Assert.Equal(2, (await Run(runner, Args(options: [option, value]))).Exit);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task TimeoutReturnsFourAndPrintsLastStatus()
    {
        var runner = new FakeRunner(Reply("waiting"));
        var (exit, output, errors) = await Run(runner, Args(options: ["--wait", "--timeout", "1", "--interval", "2"]));
        Assert.Equal(4, exit);
        Assert.Contains("접수 대기", output);
        Assert.Contains("제한시간", errors);
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetriesTwoConsecutiveSshFailuresAndStopsOnThird(bool recover)
    {
        var unavailable = new UploadProcessOutput(255, []);
        var runner = new FakeRunner(unavailable, unavailable, recover ? Reply("pending") : unavailable);
        var (exit, _, errors) = await Run(runner, Args(options: ["--wait", "--timeout", "15", "--interval", "1"]));
        Assert.Equal(recover ? 0 : 1, exit);
        Assert.Equal(recover, string.IsNullOrEmpty(errors));
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulNonterminalResponseResetsRetryCount()
    {
        var unavailable = new UploadProcessOutput(255, []);
        var runner = new FakeRunner(unavailable, unavailable, Reply("processing"), unavailable, Reply("pending"));
        Assert.Equal(0, (await Run(runner, Args(options: ["--wait", "--timeout", "15", "--interval", "1"]))).Exit);
        Assert.Equal(5, runner.Calls.Count);
    }

    [Fact]
    public async Task WaitContinuesPastThreeMissingFilesAndResetsTheFailureCount()
    {
        var missing = new UploadProcessOutput(44, []);
        var unavailable = new UploadProcessOutput(255, []);
        var runner = new FakeRunner(unavailable, unavailable, missing, missing, missing, missing, unavailable, Reply("pending"));
        var (exit, output, errors) = await Run(runner, Args(options: ["--wait", "--timeout", "20", "--interval", "1"]));
        Assert.Equal(0, exit);
        Assert.Contains("승인 대기", output);
        Assert.Empty(errors);
        Assert.Equal(8, runner.Calls.Count);
    }

    [Fact]
    public async Task MissingStatusIsAQueryFailureWithoutWaitAndTimesOutWithWait()
    {
        var missing = new UploadProcessOutput(44, []);
        Assert.Equal(1, (await Run(new(missing), Args())).Exit);
        var (exit, output, errors) = await Run(new(missing), Args(options: ["--wait", "--timeout", "1", "--interval", "2"]));
        Assert.Equal(4, exit);
        Assert.Empty(output);
        Assert.Contains("제한시간", errors);
    }

    [Fact]
    public async Task DelayedTimeoutCallbackDoesNotAllowAnotherStatusQuery()
    {
        var clock = new DelayedTimeoutClock();
        var runner = new FakeRunner(new UploadProcessOutput(44, []));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var pending = Program.UploadStatusAsync(Args(options: ["--wait", "--timeout", "2", "--interval", "1"]),
            output, errors, runner, clock);
        var completeDelay = await clock.DelayCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(2500));
        // Both timers are due. Deliver the poll delay first and withhold the deadline callback.
        completeDelay();

        Assert.Equal(4, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(runner.Calls);
        Assert.Empty(output.ToString());
        Assert.Contains("제한시간", errors.ToString());
    }

    [Fact]
    public async Task RejectsOversizeMismatchAndUntrustedFieldsWithoutPrintingRawResponse()
    {
        var valid = Reply("pending").StandardOutput;
        var text = Encoding.UTF8.GetString(valid);
        var invalidUtf8 = Encoding.UTF8.GetBytes(text.Insert(1, "\"reason\":\"broken\","));
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'b')] = 0xFF;
        foreach (var bytes in new[] { new byte[65537], Encoding.UTF8.GetBytes("{"),
            invalidUtf8,
            Reply("pending", "wrong-id").StandardOutput,
            Encoding.UTF8.GetBytes(text.Insert(1, "\"schemaVersion\":1,")),
            Encoding.UTF8.GetBytes(text.Insert(1, "\"extra\":\"Authorization private-file\",")),
            Encoding.UTF8.GetBytes(text.Insert(1, "\"reason\":\"Authorization private-file\",")) })
        {
            var runner = new FakeRunner(new UploadProcessOutput(0, bytes));
            var (exit, output, errors) = await Run(runner, Args(options: ["--format", "json"]));
            Assert.Equal(1, exit);
            Assert.Empty(output);
            Assert.DoesNotContain("private-file", errors);
            Assert.Single(runner.Calls);
        }
        var atLimit = valid.Concat(Enumerable.Repeat((byte)' ', 65536 - valid.Length)).ToArray();
        Assert.Equal(0, (await Run(new(new UploadProcessOutput(0, atLimit)), Args())).Exit);
    }

    private static UploadProcessOutput Reply(string state, string uploadId = "upload-1") =>
        new(0, JsonSerializer.SerializeToUtf8Bytes(new UploadStatus(uploadId, state, DateTimeOffset.UnixEpoch), JsonFiles.Options));

    private static string ExpectedCommand(string directory) =>
        $"if test -r {directory} && test -x {directory} && ! test -e {directory}/upload-1.json && ! test -L {directory}/upload-1.json; " +
        $"then exit 44; fi; exec cat -- {directory}/upload-1.json";

    private static async Task<(int Exit, string Output, string Errors)> Run(FakeRunner runner, string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exit = await Program.UploadAsync(args, output, errors, runner);
        return (exit, output.ToString(), errors.ToString());
    }

    private sealed class DelayedTimeoutClock : TimeProvider
    {
        private long timestamp;
        private int timerCount;
        internal TaskCompletionSource<Action> DelayCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan delta) => Interlocked.Add(ref timestamp, delta.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Increment(ref timerCount) == 2) DelayCallback.SetResult(() => callback(state));
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeRunner(params UploadProcessOutput[] responses) : IUploadProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Args, int Limit)> Calls { get; } = [];
        public bool IsAvailable(string executable) => true;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token) =>
            throw new InvalidOperationException("Status needs bounded capture.");
        public Task<UploadProcessOutput> CaptureAsync(string executable, IReadOnlyList<string> arguments, int maximumBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add((executable, arguments, maximumBytes));
            return Task.FromResult(responses[Calls.Count - 1]);
        }
    }

    private sealed class TransferRunner : IUploadProcessRunner
    {
        public bool IsAvailable(string executable) => true;
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token) =>
            Task.FromResult(0);
    }
}
