using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow : Window
{
    private TextBlock Txt(string text, double size, bool bold) => Label(text, size, Fg(), bold);
    private TextBlock Muted(string text, double size) => Label(text, size, MutedBrush());
    private TextBlock Label(string text, double size, IBrush color, bool bold = false) => new() { Text = text ?? string.Empty, FontSize = size * _preferences.TextScale, Foreground = HighContrast ? Brushes.White : color, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private IBrush Fg() => HighContrast ? Brushes.White : LauncherVisualTokens.Text(IsDeveloper);
    private IBrush MutedBrush() => HighContrast ? Brushes.White : LauncherVisualTokens.MutedText(IsDeveloper);
    private IBrush StatusBrush(string? s) { var v = s ?? string.Empty; if (v.Contains("오류")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Danger); if (v.Contains("업데이트")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Warning); if (v.Contains("설치 필요")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); if (v.Contains("최신") || v.Contains("설치")) return LauncherVisualTokens.Brush(LauncherVisualTokens.Success); return LauncherVisualTokens.Brush(LauncherVisualTokens.Accent); }
    private string ModeStatus() => IsDeveloper ? "개발자 빌드" : "안정 버전";
    private Border Card(Control child, double padding) => new() { Padding = new Thickness(padding), CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusCard), Background = LauncherVisualTokens.Surface(IsDeveloper), BorderBrush = LauncherVisualTokens.Border(IsDeveloper), BorderThickness = new Thickness(1), Child = child };
    private Button PrimaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Brushes.White);
        button.Classes.Add("posco-primary");
        button.Background = HighContrast ? Brushes.Black : LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderBrush = HighContrast ? Brushes.White : LauncherVisualTokens.Brush(LauncherVisualTokens.Accent);
        button.BorderThickness = new Thickness(1);
        return button;
    }

    private Button SecondaryButton(string text, EventHandler<RoutedEventArgs> handler, double height)
    {
        var button = BaseButton(text, handler, height, Fg());
        button.Classes.Add("posco-secondary");
        button.Background = SurfaceBrush;
        button.BorderBrush = HighContrast ? Brushes.White : LauncherVisualTokens.Border(IsDeveloper);
        button.BorderThickness = new Thickness(1);
        return button;
    }
    private Button SmallButton(string text, EventHandler<RoutedEventArgs> handler) => SecondaryButton(text, handler, 34);
    private Button BaseButton(string text, EventHandler<RoutedEventArgs> handler, double height, IBrush color)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                Foreground = color,
                FontSize = (height >= 100 ? 22 : LauncherVisualTokens.FontBody) * _preferences.TextScale,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = height >= 100 ? FontWeight.SemiBold : FontWeight.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            },
            MinHeight = height,
            MinWidth = 100,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = true,
            TabIndex = _nextTabIndex++,
            Transitions = new Transitions
            {
                new BrushTransition { Property = Button.BackgroundProperty, Duration = LauncherVisualTokens.MotionFast },
                new BrushTransition { Property = Button.BorderBrushProperty, Duration = LauncherVisualTokens.MotionFast }
            }
        };
        AutomationProperties.SetName(button, text == "↻" ? "카탈로그 새로고침" : text.TrimStart('▶', ' '));
        button.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod == NavigationMethod.Pointer) return;
            button.BorderBrush = HighContrast ? Brushes.Yellow : LauncherVisualTokens.Brush(IsDeveloper ? LauncherVisualTokens.PoscoLightBlue : LauncherVisualTokens.Accent);
            button.BorderThickness = new Thickness(3);
        };
        button.LostFocus += (_, _) => { button.BorderBrush = HighContrast ? Brushes.White : LauncherVisualTokens.Border(IsDeveloper); button.BorderThickness = new Thickness(1); };
        button.Click += handler;
        return button;
    }
    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Border Pill(string text, string bg, string fg) => new() { Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl), Background = B(bg), Child = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = B(fg), FontSize = 13, TextAlignment = TextAlignment.Center } };
    private static Control At(Control c, int col) { Grid.SetColumn(c, col); return c; }
    private static Control AtRow(Control c, int row) { Grid.SetRow(c, row); return c; }
    private static void Add(Grid g, Control c, int col) { Grid.SetColumn(c, col); g.Children.Add(c); }
    private const int MaxLogLines = 500;

    private void AppendLog(string msg, bool force = false)
    {
        var safe = DiagnosticRedactor.Redact(msg);
        _fileLogger?.Log("UI", safe);
        _presentation.Append($"{DateTime.Now:HH:mm:ss} {safe}");
        if (_logBox is null) return;
        var caret = _logBox.CaretIndex; var start = _logBox.SelectionStart; var stop = _logBox.SelectionEnd;
        var reading = _logBox.IsKeyboardFocusWithin || start != stop;
        _logBox.Text = _presentation.LogText;
        if (reading) { _logBox.CaretIndex = Math.Min(caret, _logBox.Text.Length); _logBox.SelectionStart = Math.Min(start, _logBox.Text.Length); _logBox.SelectionEnd = Math.Min(stop, _logBox.Text.Length); }
        else _logBox.CaretIndex = _logBox.Text.Length;

    }
}
