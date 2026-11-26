using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private TextBox? _developerHistoryText;
    private bool _developerHistoryErrorsOnly;
    private bool _developerRawLogExpanded;

    private Control EnterpriseLogs()
    {
        _developerHistoryText = Identify(new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 230,
            FontSize = LauncherVisualTokens.FontCaption * _preferences.TextScale
        }, "developer-history-summary", "작업 기록 요약");
        var errorsOnly = Identify(ScaleHeader(new CheckBox
        {
            Content = "오류만 보기",
            IsChecked = _developerHistoryErrorsOnly
        }), "developer-history-errors-only", "오류만 보기");
        errorsOnly.IsCheckedChanged += (_, _) =>
        {
            _developerHistoryErrorsOnly = errorsOnly.IsChecked == true;
            RefreshDeveloperHistory();
        };
        var copy = EnterpriseButton("복사", "developer-history-copy", async (_, _) =>
        {
            try
            {
                if (Clipboard is not { } clipboard) throw new InvalidOperationException();
                await clipboard.SetTextAsync(DeveloperHistoryText());
                SetStatus("작업 기록을 복사했습니다.");
            }
            catch (Exception)
            {
                SetStatus("작업 기록을 복사할 수 없습니다.");
            }
        }, tracked: false);
        var zip = EnterpriseButton("로그 ZIP 저장", "developer-history-export", (_, _) => ExportLogsZip(),
            tracked: false, icon: LauncherIconKind.Log);
        _logBox = Identify(new TextBox
        {
            Text = _presentation.LogText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 300,
            FontSize = LauncherVisualTokens.FontCaption * _preferences.TextScale,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            Foreground = Fg(),
            Background = SurfaceBrush
        }, "operation-log", "작업 기록 원문");
        var raw = Identify(ScaleHeader(new Expander
        {
            Header = "원문 보기",
            IsExpanded = _developerRawLogExpanded,
            Content = _logBox
        }), "developer-history-original", "원문 보기");
        KeyboardNavigation.SetTabNavigation(raw, KeyboardNavigationMode.Local);
        raw.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty) _developerRawLogExpanded = raw.IsExpanded;
        };
        RefreshDeveloperHistory();
        return Identify(Card(new StackPanel
        {
            Spacing = LauncherVisualTokens.SpaceMd,
            Children =
            {
                Txt("작업 기록", LauncherVisualTokens.FontSection, true),
                errorsOnly,
                _developerHistoryText,
                new WrapPanel
                {
                    ItemSpacing = LauncherVisualTokens.SpaceSm,
                    LineSpacing = LauncherVisualTokens.SpaceSm,
                    Children = { copy, zip }
                },
                raw
            }
        }), "developer-history", "작업 기록");
    }

    private string DeveloperHistoryText() => string.Join(Environment.NewLine,
        _presentation.History.Reverse()
            .Where(entry => !_developerHistoryErrorsOnly || entry.Result == LauncherHistoryResult.Failed)
            .Select(entry => entry.Text));

    private void RefreshDeveloperHistory()
    {
        if (_developerHistoryText is null) return;
        var text = DeveloperHistoryText();
        _developerHistoryText.Text = text.Length > 0 ? text : _developerHistoryErrorsOnly
            ? "오류 기록이 없습니다." : "아직 작업 기록이 없습니다.";
    }
}
