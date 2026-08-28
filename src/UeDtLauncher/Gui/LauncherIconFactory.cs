using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using IconPath = Avalonia.Controls.Shapes.Path;

namespace UeDtLauncher.Gui;

public enum LauncherIconKind
{
    Play,
    Check,
    Download,
    Warning,
    Refresh,
    Folder,
    Settings,
    Package,
    Wrench,
    Log,
    Shield
}

public static class LauncherIconFactory
{
    public static Control Create(LauncherIconKind kind, double size, IBrush color)
    {
        return new IconPath
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Data = StreamGeometry.Parse(Data(kind)),
            Stroke = color,
            StrokeThickness = kind == LauncherIconKind.Play ? 0 : 1.9,
            Fill = kind == LauncherIconKind.Play ? color : null,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round
        };
    }

    private static string Data(LauncherIconKind kind) => kind switch
    {
        LauncherIconKind.Play => "M7,4 L20,12 L7,20 Z",
        LauncherIconKind.Check => "M4,12 L9,17 L20,6",
        LauncherIconKind.Download => "M12,3 L12,15 M6,10 L12,16 L18,10 M5,21 L19,21",
        LauncherIconKind.Warning => "M12,3 L22,20 L2,20 Z M12,9 L12,14 M12,17 L12,17.2",
        LauncherIconKind.Refresh => "M20,7 L20,3 L16,3 M20,3 C16,-1 8,0 5,6 M4,17 L4,21 L8,21 M4,21 C8,25 16,24 19,18",
        LauncherIconKind.Folder => "M3,6 L9,6 L11,9 L21,9 L21,20 L3,20 Z",
        LauncherIconKind.Settings => "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 M12,2 L12,5 M12,19 L12,22 M2,12 L5,12 M19,12 L22,12 M5,5 L7,7 M17,17 L19,19 M19,5 L17,7 M7,17 L5,19",
        LauncherIconKind.Package => "M3,7 L12,2 L21,7 L12,12 Z M3,7 L3,17 L12,22 L12,12 M21,7 L21,17 L12,22",
        LauncherIconKind.Wrench => "M14,6 A5,5 0 0 0 7,12 L2,17 L7,22 L12,17 A5,5 0 0 0 18,10 L14,14 L10,10 Z",
        LauncherIconKind.Log => "M5,3 L19,3 L19,21 L5,21 Z M8,8 L16,8 M8,12 L16,12 M8,16 L13,16",
        LauncherIconKind.Shield => "M12,2 L20,5 L20,11 C20,17 16,21 12,23 C8,21 4,17 4,11 L4,5 Z M8,12 L11,15 L16,9",
        _ => "M4,12 L20,12"
    };
}
