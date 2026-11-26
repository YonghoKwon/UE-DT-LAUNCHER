using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class Prod08AStatusTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [AvaloniaTheory]
    [InlineData(GeneralLauncherState.Checking, "상태 확인 중", false, "상태를 확인", "")]
    [InlineData(GeneralLauncherState.NotInstalled, "설치", true, "처음 설치", "")]
    [InlineData(GeneralLauncherState.UpdateAvailable, "업데이트", true, "업데이트할 수", "")]
    [InlineData(GeneralLauncherState.Ready, "실행", true, "파일이 확인", "")]
    [InlineData(GeneralLauncherState.RuntimeBlocked, "프로그램 종료 후 다시 확인", false, "실행 중", "")]
    [InlineData(GeneralLauncherState.UpdateAvailable, "문제 해결", true, "일부 파일이 손상", "damage")]
    [InlineData(GeneralLauncherState.RecoverableError, "다시 확인", true, "서버에 연결할 수 없습니다", "")]
    [InlineData(GeneralLauncherState.UpdateAvailable, "권장 버전 변경 안내", true, "이전 설치 기록보다 낮습니다", "lower")]
    [InlineData(GeneralLauncherState.Working, "작업 중", false, "진행하고 있습니다", "")]
    public void MainStateHasOneNamedPrimaryAction(GeneralLauncherState state, string name, bool enabled, string guidance, string variant)
    {
        var model = Model(state, guidance);
        model.ProjectStatus = variant switch
        {
            "damage" => new(true, "1.0.0", "1.0.0", true, 0, 2, false),
            "lower" => new(false, null, "1.0.0", true, 0, 0, false,
                new(new("demo", "prod", "stable", "windows-x64", "2.0.0"), "2026-11-26T01:00:00Z")),
            _ => null
        };
        var window = Window(model);
        window.Show();
        try
        {
            var primary = Find<Button>(window, "primary-action");
            Assert.Equal(name, AutomationProperties.GetName(primary));
            Assert.Equal(enabled, primary.IsEnabled);
            Assert.Contains(guidance, Find<TextBlock>(window, "project-state-guidance").Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(GeneralLauncherState.NotInstalled)]
    [InlineData(GeneralLauncherState.Ready)]
    [InlineData(GeneralLauncherState.RecoverableError)]
    public void IdleDashboardHidesProgressEvenAfterAStatusCheck(GeneralLauncherState state)
    {
        var window = Window(Model(state));
        window.Show();
        try
        {
            var presentation = Presentation(window);
            presentation.Begin(LauncherUiOperation.Check);
            presentation.Complete("상태 확인");
            Invoke(window, "RefreshPresentation");
            Assert.False(Find<ProgressBar>(window, "operation-progress").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "100%");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SuccessRemainsUntilTheNextStatusCheck()
    {
        var window = Window(Model(GeneralLauncherState.Ready));
        window.Show();
        try
        {
            var presentation = Presentation(window);
            presentation.Begin(LauncherUiOperation.Update);
            presentation.InstallationNotice = "제철소 DT 1.0.0 설치를 마쳤습니다.";
            presentation.Complete("설치 완료");
            Invoke(window, "Build");
            Assert.Equal(100, Find<ProgressBar>(window, "operation-progress").Value);
            Assert.True(Find<ProgressBar>(window, "operation-progress").IsEffectivelyVisible);
            Assert.True(Find<TextBlock>(window, "installation-completed").IsEffectivelyVisible);

            presentation.Begin(LauncherUiOperation.Check);
            presentation.Complete("최신 상태");
            Invoke(window, "Build");
            Assert.False(Find<ProgressBar>(window, "operation-progress").IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants(), control => Id(control) == "installation-completed");
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(200, 500L, 12, 9_999, "계산 중")]
    [InlineData(200, 500L, 12, 10_000, "약 25초")]
    [InlineData(200, 500L, 0, 10_000, "계산 중")]
    [InlineData(200, null, 12, 10_000, "계산 중")]
    public void RemainingTimeUsesMeasuredNetworkRate(long received, long? total, double rate, long sample, string expected) =>
        Assert.Equal(expected, MainWindow.RemainingDownloadTime(received, total, rate, sample));

    [AvaloniaTheory]
    [InlineData("authentication-failed", "인증 정보를 확인")]
    [InlineData("access-denied", "받을 권한이 없습니다")]
    [InlineData("no-authorized-release", "아직 공개된 버전이 없습니다")]
    [InlineData("server-unavailable", "서버에 연결할 수 없습니다")]
    [InlineData("disk-full", "설치할 공간이 부족합니다")]
    [InlineData("runtime-blocked", "닫으면 업데이트할 수 있습니다")]
    [InlineData("damaged-installation", "일부 파일이 손상됐습니다")]
    [InlineData("rate-limited", "다시")]
    [InlineData("not-configured", "관리자에게 문의해 주세요")]
    [InlineData("unknown", "작업을 완료하지 못했습니다")]
    public void ErrorsShowActionsAndDetailsWithoutExceptionText(string code, string message)
    {
        const string hidden = @"Bearer secret-fixture-token C:\Users\fixture-user\password.txt";
        var model = Model(GeneralLauncherState.Ready);
        var window = code == "not-configured"
            ? new MainWindow(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), LauncherConfigSource.Missing, false), null, new(), false)
            : Window(model);
        window.Show();
        try
        {
            if (code == "damaged-installation")
            {
                model.ApplyProjectStatus(new(true, "1.0.0", "1.0.0", true, 1, 0, false));
                Invoke(window, "Build");
            }
            else if (code != "not-configured")
            {
                Exception error = code switch
                {
                    "authentication-failed" => new HttpRequestException(hidden, null, HttpStatusCode.Unauthorized),
                    "access-denied" => new HttpRequestException(hidden, null, HttpStatusCode.Forbidden),
                    "disk-full" => new IOException("Not enough free disk space on " + hidden),
                    "runtime-blocked" => new RuntimeBlockedException(new(RuntimeState.Running, code, hidden)),
                    "unknown" => new Exception(hidden),
                    _ => new AgentOperationException(code, hidden, hidden, 5)
                };
                Invoke(window, "MarkError", error, "작업 실패", false);
            }

            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text?.Contains(message) == true);
            Assert.True(Find<Button>(window, "copy-support-info").IsEnabled);
            var primary = Find<Button>(window, code == "runtime-blocked" ? "status-check" : "primary-action");
            Assert.True(primary.IsEnabled);
            var details = Find<Expander>(window, "error-details");
            Assert.False(details.IsExpanded);
            if (code == "not-configured") Assert.False(Find<TextBox>(window, "configuration-example").IsEffectivelyVisible);
            details.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Button>(window, "save-error-logs").IsEffectivelyVisible);
            Assert.Contains(code, Find<TextBlock>(window, "safe-error-details").Text);
            var shown = string.Join("\n", details.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            var copied = MainWindow.SupportInfo(Presentation(window).SupportId, DateTimeOffset.Now);
            foreach (var value in new[] { shown, copied })
            {
                Assert.DoesNotContain("secret-fixture-token", value);
                Assert.DoesNotContain("fixture-user", value);
            }
            Assert.Contains(Environment.MachineName, copied);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void OlderAgentFramesOmitTheOptionalSignal()
    {
        var value = JsonSerializer.Deserialize<ManagedAgentProgress>("""{"stage":"DownloadProgress","message":"","percent":35} """, JsonFiles.Options)!;
        Assert.Null(value.ToLauncherProgress().PauseAvailable);
        var encoded = JsonSerializer.Serialize(value with { PauseAvailable = true }, JsonFiles.Options);
        Assert.True(JsonSerializer.Deserialize<ManagedAgentProgress>(encoded, JsonFiles.Options)!.ToLauncherProgress().PauseAvailable);
        Assert.Null(JsonSerializer.Deserialize<ManagedAgentResponse>("{}", JsonFiles.Options)!.RetainedPartialDownload);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void PauseRequiresSignalAndCommitDisablesCancellation(bool? available)
    {
        var model = Model(GeneralLauncherState.Ready);
        var window = Window(model);
        using var cancellation = new CancellationTokenSource();
        window.Show();
        try
        {
            model.Running = true;
            Presentation(window).Begin(LauncherUiOperation.Update);
            Invoke(window, "StartCancellableUiOperation", cancellation);
            Invoke(window, "EngineProgress", new LauncherProgress("DownloadProgress", "", BytesDownloaded: 32768,
                TotalBytes: 65536, PauseAvailable: available));
            Assert.Equal(available == true, window.GetVisualDescendants().Any(control => Id(control) == "pause-operation"));
            Assert.True(Find<Button>(window, "cancel-operation").IsEnabled);
            Assert.Contains("32 KB / 64 KB", Find<TextBlock>(window, "operation-status").Text);

            Invoke(window, "EngineProgress", new LauncherProgress("Apply", ""));
            Assert.False(Find<Button>(window, "cancel-operation").IsEnabled);
            Assert.True(Find<TextBlock>(window, "commit-guidance").IsVisible);
            Invoke(window, "RequestOperationCancellation");
            Assert.False(cancellation.IsCancellationRequested);
        }
        finally { window.Close(); }
    }

    private static LauncherDashboardViewModel Model(GeneralLauncherState state, string? guidance = null) => new(LauncherEdition.General)
    {
        GeneralState = state,
        InstallDetail = guidance ?? "설치 파일이 확인되었습니다.",
        Config = new LauncherConfig { ProjectId = "demo", Projects = [new() { ProjectId = "demo", DisplayName = "제철소 DT" }] }
    };

    private static MainWindow Window(LauncherDashboardViewModel model) => new(new("fixture.json", LauncherConfigSource.Explicit, false), model, new(), false);
    private static LauncherOperationPresentation Presentation(MainWindow window) => (LauncherOperationPresentation)typeof(MainWindow).GetField("_presentation", Private)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string action, params object[] args)
    {
        typeof(MainWindow).GetMethod(action, Private)!.Invoke(window, args);
        Dispatcher.UIThread.RunJobs();
    }
    private static string? Id(Avalonia.Visual visual) => visual is Control control ? AutomationProperties.GetAutomationId(control) : null;
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => Id(control) == id);
}
