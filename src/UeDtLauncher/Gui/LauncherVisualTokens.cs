using Avalonia.Media;

namespace UeDtLauncher.Gui;

public static class LauncherVisualTokens
{
    public const double SpaceXs = 4;
    public const double SpaceSm = 8;
    public const double SpaceMd = 12;
    public const double SpaceLg = 16;
    public const double SpaceXl = 24;
    public const double Space2Xl = 32;

    public const double RadiusControl = 10;
    public const double RadiusCard = 13;
    public const double RadiusHero = 16;

    public const double FontCaption = 12;
    public const double FontBody = 14;
    public const double FontStatus = 16;
    public const double FontSection = 20;
    public const double FontProject = 28;

    public static readonly TimeSpan MotionFast = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan MotionNormal = TimeSpan.FromMilliseconds(180);

    public static readonly Color LightBackground = Color.Parse("#F6F8FC");
    public static readonly Color LightSurface = Color.Parse("#FFFFFF");
    public static readonly Color LightSurfaceMuted = Color.Parse("#EEF2F7");
    public static readonly Color LightBorder = Color.Parse("#DDE3EC");
    public static readonly Color LightText = Color.Parse("#172033");
    public static readonly Color LightMutedText = Color.Parse("#667085");

    public static readonly Color DarkBackground = Color.Parse("#0B1220");
    public static readonly Color DarkSurface = Color.Parse("#111827");
    public static readonly Color DarkSurfaceRaised = Color.Parse("#172033");
    public static readonly Color DarkBorder = Color.Parse("#2A3649");
    public static readonly Color DarkText = Color.Parse("#F8FAFC");
    public static readonly Color DarkMutedText = Color.Parse("#A8B3C5");

    public static readonly Color BrandNavy = Color.Parse("#0F2A56");
    public static readonly Color BrandNavyDeep = Color.Parse("#091B39");
    public static readonly Color Accent = Color.Parse("#2563EB");
    public static readonly Color AccentHover = Color.Parse("#1D4ED8");
    public static readonly Color AccentSoft = Color.Parse("#E8F0FF");

    public static readonly Color Success = Color.Parse("#166534");
    public static readonly Color SuccessSoft = Color.Parse("#E9F8EE");
    public static readonly Color Warning = Color.Parse("#A24B08");
    public static readonly Color WarningSoft = Color.Parse("#FFF3E3");
    public static readonly Color Danger = Color.Parse("#B42318");
    public static readonly Color DangerSoft = Color.Parse("#FDECEC");

    public static IBrush Brush(Color color) => new SolidColorBrush(color);
    public static IBrush Background(bool dark) => Brush(dark ? DarkBackground : LightBackground);
    public static IBrush Surface(bool dark) => Brush(dark ? DarkSurface : LightSurface);
    public static IBrush SurfaceMuted(bool dark) => Brush(dark ? DarkSurfaceRaised : LightSurfaceMuted);
    public static IBrush Border(bool dark) => Brush(dark ? DarkBorder : LightBorder);
    public static IBrush Text(bool dark) => Brush(dark ? DarkText : LightText);
    public static IBrush MutedText(bool dark) => Brush(dark ? DarkMutedText : LightMutedText);

    public static double ContrastRatio(Color foreground, Color background)
    {
        var lighter = Math.Max(Luminance(foreground), Luminance(background));
        var darker = Math.Min(Luminance(foreground), Luminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
