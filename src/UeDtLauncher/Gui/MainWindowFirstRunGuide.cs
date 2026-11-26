using Avalonia.Controls;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _preferenceWrites = new(1);
    private bool _firstRunGuideShown;

    internal async Task<Window?> ShowFirstRunGuideAsync(string? preferencesPath = null)
    {
        if (IsDeveloper || !IsVisible || _firstRunGuideShown || _preferences.HasSeenFirstRunGuide)
            return null;

        var dialog = ShowFirstRunGuide(this);
        _firstRunGuideShown = true;
        try
        {
            await SavePreferencesAsync(value => value with { HasSeenFirstRunGuide = true }, preferencesPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            AppendLog("첫 실행 안내를 본 기록을 저장하지 못했습니다.", true);
        }
        return dialog;
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

    private Task SaveDisplayPreferencesAsync(int scaleIndex, bool highContrast) =>
        SavePreferencesAsync(value => value with
        {
            TextScale = LauncherUiPreferences.SupportedScales[scaleIndex],
            HighContrast = highContrast
        });

    private async Task SavePreferencesAsync(Func<LauncherUiPreferences, LauncherUiPreferences> update, string? path = null)
    {
        await _preferenceWrites.WaitAsync();
        try
        {
            var value = update(_preferences);
            // The writer flushes the temporary file to disk before replacing the preferences.
            await Task.Run(() => value.SaveAsync(path));
            _preferences = value;
        }
        finally { _preferenceWrites.Release(); }
    }
}
