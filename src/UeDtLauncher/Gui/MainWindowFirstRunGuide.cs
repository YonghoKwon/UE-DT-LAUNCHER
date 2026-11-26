using Avalonia.Controls;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private async Task ShowFirstRunGuideAsync()
    {
        if (IsDeveloper || !IsVisible) return;
        try
        {
            var (_, claimed) = await LauncherUiPreferences.UpdateAsync(_preferencesPath,
                latest => latest with { HasSeenFirstRunGuide = true });
            if (claimed && IsVisible)
                ShowFirstRunGuide(this).Closed += (_, _) => Dispatcher.UIThread.Post(OfferRecommendationChange);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("첫 실행 안내를 본 기록을 저장하지 못했습니다.", true);
        }
    }

    private Window ShowFirstRunGuide(Window owner)
    {
        var steps = new StackPanel { Spacing = LauncherVisualTokens.SpaceLg };
        steps.Children.Add(GuideStep("설치", "프로그램을 고르고 [설치]를 누르세요."));
        steps.Children.Add(GuideStep("업데이트", "새 버전이 나오면 [업데이트] 버튼이 나타납니다."));
        steps.Children.Add(GuideStep("지원 정보 복사", "문제가 생기면 [설정 → 정보 → 지원 정보 복사]로 정보를 복사해 담당자에게 보내세요."));
        if (SelectedInstallDirectory() is { } installDirectory)
            steps.Children.Add(KeyValue("설치 위치", installDirectory));

        Window? dialog = null;
        var start = Identify(PrimaryButton("시작하기", (_, _) => dialog!.Close(), 40), "guide-start", "시작하기");
        dialog = AccessibleDialog(owner, "DT Launcher에 오신 것을 환영합니다", steps, start);
        _ = dialog.ShowDialog(owner);
        return dialog;
    }

    private Control GuideStep(string title, string description) =>
        DialogCard(new StackPanel
        {
            Spacing = LauncherVisualTokens.SpaceSm,
            Children = { Txt(title, LauncherVisualTokens.FontSection, true), Txt(description, LauncherVisualTokens.FontBody, false) }
        });

    private async Task SaveDisplayPreferencesAsync(LauncherUiPreferences openedWith, int scaleIndex, bool highContrast,
        bool keepInTray, int trayCheckMinutes)
    {
        var scale = LauncherUiPreferences.SupportedScales[scaleIndex];
        var (value, _) = await LauncherUiPreferences.UpdateAsync(_preferencesPath, latest => latest with
        {
            TextScale = scale != openedWith.TextScale ? scale : latest.TextScale,
            HighContrast = highContrast != openedWith.HighContrast ? highContrast : latest.HighContrast,
            KeepInTray = keepInTray != openedWith.KeepInTray ? keepInTray : latest.KeepInTray,
            TrayCheckMinutes = trayCheckMinutes != openedWith.TrayCheckMinutes ? trayCheckMinutes : latest.TrayCheckMinutes
        });
        var intervalChanged = value.TrayCheckMinutes != _preferences.TrayCheckMinutes;
        _preferences = value with { HasSeenTrayNotice = value.HasSeenTrayNotice || _preferences.HasSeenTrayNotice };
        if (intervalChanged && _agentStatusTimer.IsEnabled) StartStatusPolling();
    }
}
