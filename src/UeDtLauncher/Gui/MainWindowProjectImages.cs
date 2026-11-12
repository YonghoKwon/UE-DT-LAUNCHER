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
    private Control Hero(double h)
    {
        var hero = new Grid { Height = h };
        var asset = ProjectVisualResolver.Resolve(_selectedProject, ConfigPath, ProjectVisualKind.Hero);
        hero.Children.Add(ProjectHeroVisual(asset));
        hero.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusHero),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(230, 9, 27, 57), 0),
                    new GradientStop(Color.FromArgb(135, 15, 42, 86), 0.55),
                    new GradientStop(Color.FromArgb(35, 15, 42, 86), 1)
                }
            }
        });
        hero.Children.Add(new StackPanel { Spacing = 8, Margin = new Thickness(30), VerticalAlignment = VerticalAlignment.Bottom, Children = { Label(_selectedProject.DisplayName, IsDeveloper ? 28 : LauncherVisualTokens.FontProject, Brushes.White, true), Label(IsDeveloper ? $"개발자 모드 · {_config.Environment} · {_config.Channel} · {CurrentPlatform}" : $"운영 안정화 · stable · {CurrentPlatform}", LauncherVisualTokens.FontBody, B("#DCE8FF")), Label(_releaseNotes, LauncherVisualTokens.FontCaption, B("#EDF2FA")) } });
        return hero;
    }

    private Control ProjectHeroVisual(ProjectVisualAsset asset)
    {
        var bitmap = CachedProjectBitmap(asset);
        if (bitmap is not null)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusHero),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            };
        }

        return BrandedFallback(asset, hero: true);
    }

    private Control ProjectThumbnail(ProjectUiConfig project)
    {
        var asset = ProjectVisualResolver.Resolve(project, ConfigPath, ProjectVisualKind.Thumbnail);
        var bitmap = CachedProjectBitmap(asset);
        if (bitmap is not null)
        {
            return new Border
            {
                Width = 54,
                Height = 54,
                CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
            };
        }

        var fallback = BrandedFallback(asset, hero: false);
        fallback.Width = 54;
        fallback.Height = 54;
        return fallback;
    }

    private Border BrandedFallback(ProjectVisualAsset asset, bool hero)
    {
        var accents = new[]
        {
            LauncherVisualTokens.Accent,
            LauncherVisualTokens.PoscoLightBlue,
            LauncherVisualTokens.BrandNavyDeep
        };
        var accent = accents[Math.Clamp(asset.FallbackVariant, 0, accents.Length - 1)];
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(LauncherVisualTokens.BrandNavyDeep, 0),
                    new GradientStop(LauncherVisualTokens.BrandNavy, 0.55),
                    new GradientStop(accent, 1)
                }
            }
        });
        grid.Children.Add(new Border
        {
            Width = hero ? 360 : 44,
            Height = hero ? 360 : 44,
            CornerRadius = new CornerRadius(hero ? 180 : 22),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, hero ? 70 : -10, 0),
            Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255))
        });
        grid.Children.Add(new TextBlock
        {
            Text = asset.Initials,
            FontSize = hero ? 32 : 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(hero ? (byte)55 : (byte)220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        });
        return new Border
        {
            CornerRadius = new CornerRadius(hero ? LauncherVisualTokens.RadiusHero : LauncherVisualTokens.RadiusControl),
            ClipToBounds = true,
            Child = grid
        };
    }

    private Bitmap? CachedProjectBitmap(ProjectVisualAsset asset)
    {
        if (!asset.HasImage || asset.ResolvedPath is null) return null;
        if (_projectVisualCache.TryGetValue(asset.ResolvedPath, out var cached)) return cached;
        try
        {
            var bitmap = new Bitmap(asset.ResolvedPath);
            _projectVisualCache[asset.ResolvedPath] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }


}
