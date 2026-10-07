using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ReadinessUiTests
{
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public void MainUserErrorIncludesBothCauseAndAdministratorAction(System.Net.HttpStatusCode status)
    {
        var error = LauncherUiError.From(new HttpRequestException("Bearer sentinel", null, status));
        Assert.Contains("관리자", error.Message);
        Assert.DoesNotContain("sentinel", error.Message);
        Assert.NotNull(error.SupportId);
    }
    [AvaloniaTheory]
    [InlineData("portable")]
    [InlineData("managed-agent")]
    public void DiagnosticDialogDoesNotChangeMainInstallationOrRuntimeState(string mode)
    {
        var model = new LauncherDashboardViewModel { Config = new() { ProjectId = "demo", DeploymentMode = mode }, GeneralState = GeneralLauncherState.RuntimeBlocked };
        var report = DoctorPresentation.Complete(new("now", false, "1", "test", [DoctorPresentation.Failure("catalog", new HttpRequestException("sentinel-secret", null, System.Net.HttpStatusCode.Unauthorized), "agent")]) { SupportId = "support-proof" });
        var calls = 0;
        var window = new MainWindow(new("missing.json", LauncherConfigSource.Explicit, false), model, new(), false,
            doctor: (_, online, _, _) => { Assert.False(online); calls++; return Task.FromResult(report); });
        window.Show(); var dialog = window.ShowReadinessDialog();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, calls); Assert.Equal(GeneralLauncherState.RuntimeBlocked, model.GeneralState);
            var text = string.Join('\n', dialog.GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text));
            Assert.Contains("PC 인증", text); Assert.DoesNotContain("sentinel-secret", text);
            Assert.Equal("support-proof", dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text);
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public void DiagnosticExceptionShowsActionOnceWithoutSecret()
    {
        var model = new LauncherDashboardViewModel { Config = new() { ProjectId = "demo" } };
        var window = new MainWindow(new("missing.json", LauncherConfigSource.Explicit, false), model, new(), false,
            doctor: (_, _, _, _) => Task.FromException<DoctorReport>(new HttpRequestException("Bearer sentinel", null, System.Net.HttpStatusCode.Forbidden)));
        window.Show(); var dialog = window.ShowReadinessDialog();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var text = string.Join('\n', dialog.GetLogicalDescendants().OfType<TextBlock>().Select(x => x.Text));
            var action = LauncherGuidance.For("access-denied").NextAction;
            Assert.Equal(1, text.Split(action, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("sentinel", text);
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateDiagnosticResultIsDiscardedAfterSelectionChange()
    {
        var pending = new TaskCompletionSource<DoctorReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new LauncherDashboardViewModel { Config = new() { ProjectId = "demo" } };
        var window = new MainWindow(new("missing.json", LauncherConfigSource.Explicit, false), model, new(), false,
            doctor: (_, _, _, _) => pending.Task);
        window.Show(); var dialog = window.ShowReadinessDialog();
        try
        {
            model.Config.Channel = "beta";
            pending.SetResult(DoctorPresentation.Complete(new("now", true, "1", "test", []) { SupportId = "old-selection" }));
            await pending.Task; await Task.Yield(); Dispatcher.UIThread.RunJobs();
            var summary = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(x => AutomationProperties.GetAutomationId(x) == "readiness-summary");
            Assert.Contains("선택", summary.Text);
            Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<TextBox>(), x => x.Text == "old-selection");
        }
        finally { dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingDiagnosticDialogCancelsOnlyItsRequest()
    {
        CancellationToken captured = default;
        var model = new LauncherDashboardViewModel { Config = new() { ProjectId = "demo" } };
        var window = new MainWindow(new("missing.json", LauncherConfigSource.Explicit, false), model, new(), false,
            doctor: (_, _, _, token) => { captured = token; return Task.Delay(Timeout.Infinite, token).ContinueWith<DoctorReport>(_ => throw new OperationCanceledException(token), token); });
        window.Show(); var dialog = window.ShowReadinessDialog();
        dialog.Close(); Dispatcher.UIThread.RunJobs();
        Assert.True(captured.IsCancellationRequested); Assert.False(model.Running);
        window.Close();
    }
}
