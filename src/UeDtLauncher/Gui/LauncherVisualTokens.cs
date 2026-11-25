using Avalonia.Media;

namespace UeDtLauncher.Gui;

internal enum LauncherStatusTone
{
    Normal,
    Caution,
    Error,
    Progress
}

public static class LauncherVisualTokens
{
    public const double SpaceXs = 4;
    public const double SpaceSm = 8;
    public const double SpaceMd = 12;
    public const double SpaceLg = 16;
    public const double SpaceXl = 24;
    public const double Space2Xl = 32;

    public const double RadiusControl = 8;
    public const double RadiusCard = 10;
    public const double RadiusHero = 16;

    public const double FontCaption = 12;
    public const double FontSecondary = 13;
    public const double FontBody = 14;
    public const double FontSection = 20;
    public const double FontProject = 28;
    public const double FontStatus = FontBody;

    public static readonly TimeSpan MotionFast = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan MotionNormal = TimeSpan.FromMilliseconds(180);

    public static readonly Color LightBackground = Color.Parse("#F3F6F8");
    public static readonly Color LightSurface = Color.Parse("#FFFFFF");
    public static readonly Color LightSurfaceMuted = Color.Parse("#EEF2F7");
    public static readonly Color LightBorder = Color.Parse("#7A8794");
    public static readonly Color LightText = Color.Parse("#172033");
    public static readonly Color LightMutedText = Color.Parse("#596579");

    public static readonly Color DarkBackground = Color.Parse("#0B1220");
    public static readonly Color DarkSurface = Color.Parse("#111827");
    public static readonly Color DarkSurfaceRaised = Color.Parse("#172033");
    public static readonly Color DarkBorder = Color.Parse("#718096");
    public static readonly Color DarkText = Color.Parse("#F8FAFC");
    public static readonly Color DarkMutedText = Color.Parse("#A8B3C5");

    public static readonly Color BrandNavy = Color.Parse("#05507D");
    public static readonly Color BrandNavyDeep = Color.Parse("#091B39");
    public static readonly Color Accent = Color.Parse("#05507D");
    public static readonly Color AccentHover = Color.Parse("#043E62");
    public static readonly Color AccentSoft = Color.Parse("#E6F1F7");
    public static readonly Color PoscoLightBlue = Color.Parse("#00A5E5");

    public static readonly Color Success = Color.Parse("#166534");
    public static readonly Color SuccessSoft = Color.Parse("#E9F8EE");
    public static readonly Color Warning = Color.Parse("#A24B08");
    public static readonly Color WarningSoft = Color.Parse("#FFF3E3");
    public static readonly Color Danger = Color.Parse("#B42318");
    public static readonly Color DangerSoft = Color.Parse("#FDECEC");
    private static readonly Color DarkSuccess = Color.Parse("#86E5A8");
    private static readonly Color DarkWarning = Color.Parse("#FFCA82");
    private static readonly Color DarkDanger = Color.Parse("#FFADA8");
    private static readonly Color DarkProgress = Color.Parse("#87D2FF");
    private static readonly Color ContrastSuccess = Color.Parse("#A3FFBC");
    private static readonly Color ContrastWarning = Color.Parse("#FFDF70");
    private static readonly Color ContrastDanger = Color.Parse("#FFB4B4");
    private static readonly Color ContrastProgress = Color.Parse("#9FE7FF");

    internal static readonly Color LightSkeleton = Color.Parse("#778395");
    internal static readonly Color DarkSkeleton = Color.Parse("#78869A");
    internal static readonly Color ContrastSkeleton = Color.Parse("#A0A0A0");
    private static readonly Color FallbackBlue = Color.Parse("#164E73");
    private static readonly Color FallbackTeal = Color.Parse("#124F58");

    internal static Color ProjectTileColor(int variant, bool highContrast) => (variant, highContrast) switch
    {
        (_, true) => Colors.Black,
        (1, _) => FallbackBlue,
        (2, _) => FallbackTeal,
        _ => BrandNavyDeep
    };

    internal static Color PageColor(bool dark, bool highContrast) =>
        highContrast ? Colors.Black : dark ? DarkBackground : LightBackground;
    internal static Color SurfaceColor(bool dark, bool highContrast) =>
        highContrast ? Colors.Black : dark ? DarkSurface : LightSurface;
    internal static Color MutedSurfaceColor(bool dark, bool highContrast) =>
        highContrast ? Colors.Black : dark ? DarkSurfaceRaised : LightSurfaceMuted;
    internal static Color TextColor(bool dark, bool highContrast) =>
        highContrast ? Colors.White : dark ? DarkText : LightText;
    internal static Color MutedTextColor(bool dark, bool highContrast) =>
        highContrast ? Colors.White : dark ? DarkMutedText : LightMutedText;
    internal static Color BorderColor(bool dark, bool highContrast) =>
        highContrast ? Colors.White : dark ? DarkBorder : LightBorder;
    internal static Color PrimaryBorderColor(bool dark, bool highContrast) =>
        highContrast || dark ? BorderColor(dark, highContrast) : Accent;
    internal static Color FocusColor(bool dark, bool highContrast) =>
        highContrast ? Colors.Yellow : dark ? PoscoLightBlue : Accent;
    internal static Color SkeletonColor(bool dark, bool highContrast) =>
        highContrast ? ContrastSkeleton : dark ? DarkSkeleton : LightSkeleton;

    internal static Color StatusColor(LauncherStatusTone tone, bool dark, bool highContrast) => (tone, dark, highContrast) switch
    {
        (LauncherStatusTone.Normal, _, true) => ContrastSuccess,
        (LauncherStatusTone.Caution, _, true) => ContrastWarning,
        (LauncherStatusTone.Error, _, true) => ContrastDanger,
        (_, _, true) => ContrastProgress,
        (LauncherStatusTone.Normal, true, _) => DarkSuccess,
        (LauncherStatusTone.Caution, true, _) => DarkWarning,
        (LauncherStatusTone.Error, true, _) => DarkDanger,
        (_, true, _) => DarkProgress,
        (LauncherStatusTone.Normal, _, _) => Success,
        (LauncherStatusTone.Caution, _, _) => Warning,
        (LauncherStatusTone.Error, _, _) => Danger,
        _ => Accent
    };

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
