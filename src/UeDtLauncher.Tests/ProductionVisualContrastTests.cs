using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;
using IconPath = Avalonia.Controls.Shapes.Path;

namespace UeDtLauncher.Tests;

public class ProductionVisualContrastTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DashboardColorsMeetTextAndControlContrast(bool developer, bool highContrast)
    {
        var surfaces = new[]
        {
            LauncherVisualTokens.PageColor(developer, highContrast),
            LauncherVisualTokens.SurfaceColor(developer, highContrast),
            LauncherVisualTokens.MutedSurfaceColor(developer, highContrast)
        };
        var tones = Enum.GetValues<LauncherStatusTone>()
            .Select(tone => LauncherVisualTokens.StatusColor(tone, developer, highContrast))
            .ToArray();
        Assert.Equal(4, tones.Distinct().Count());

        foreach (var surface in surfaces)
        {
            Meets(LauncherVisualTokens.TextColor(developer, highContrast), surface, 4.5);
            Meets(LauncherVisualTokens.MutedTextColor(developer, highContrast), surface, 4.5);
            foreach (var tone in tones) Meets(tone, surface, 4.5);
            Meets(LauncherVisualTokens.BorderColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.FocusColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.PrimaryBorderColor(developer, highContrast), surface, 3);
            Meets(LauncherVisualTokens.SkeletonColor(developer, highContrast), surface, 3);
        }

        Meets(Colors.White, highContrast ? Colors.Black : LauncherVisualTokens.Accent, 4.5);
        for (var variant = 0; variant < 3; variant++)
            Meets(Colors.White, LauncherVisualTokens.ProjectTileColor(variant, highContrast), 4.5);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AppliedDashboardBrushesMeetContrast(bool developer, bool highContrast)
    {
        var model = new LauncherDashboardViewModel(developer ? LauncherEdition.Developer : LauncherEdition.General)
        {
            Config = new LauncherConfig
            {
                ProjectId = "demo",
                Projects =
                [
                    new() { ProjectId = "another", DisplayName = "다른 프로젝트" },
                    new() { ProjectId = "demo", DisplayName = "프로젝트 DT" }
                ]
            }
        };
        var window = new MainWindow(new("/nonexistent/contrast.json", LauncherConfigSource.Missing, false),
            model, new(1, highContrast), false);
        window.Show();
        window.Width = 1280;
        try
        {
            foreach (var state in new[]
            {
                GeneralLauncherState.Ready, GeneralLauncherState.UpdateAvailable, GeneralLauncherState.RecoverableError,
                GeneralLauncherState.Checking
            })
            {
                model.GeneralState = state;
                Rebuild(window);
                List<Control> appControls = window.GetLogicalDescendants().OfType<Control>().ToList();
                foreach (var expander in appControls.OfType<Expander>()) expander.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();

                CheckAppliedBrushes(window);
                if (developer)
                {
                    var release = appControls.OfType<Border>().Single(b => Id(b) == "release-info");
                    Assert.Equal(LauncherVisualTokens.SurfaceColor(developer, highContrast), Solid(release.Background));
                    Assert.Equal(LauncherVisualTokens.BorderColor(developer, highContrast), Solid(release.BorderBrush));
                }
            }

            window.Width = 1000; // Exercise the compact selector and developer tabs.
            Dispatcher.UIThread.RunJobs();
            CheckAppliedBrushes(window);
            var dialog = window.ShowEnterpriseSettings(developer);
            try
            {
                Dispatcher.UIThread.RunJobs();
                CheckAppliedBrushes(dialog);
            }
            finally { dialog.Close(); }
        }
        finally { window.Close(); }
    }

    private static void Rebuild(MainWindow window)
    {
        typeof(MainWindow).GetMethod("Build", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckAppliedBrushes(Window window)
    {
        // Items owned by the launcher are taken from the logical tree. Their
        // actual adjacent backgrounds come from the composed visual tree.
        var controls = window.GetLogicalDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.GetVisualRoot() == window).ToArray();
        Assert.NotEmpty(controls.OfType<TextBlock>());
        foreach (var control in controls)
        {
            switch (control)
            {
                case TextBlock { Text.Length: > 0, Opacity: > 0 } text:
                    Meets(text.Foreground, Backdrop(text), 4.5, text.Text);
                    break;
                case TextBox box:
                    Meets(box.Foreground, Backdrop(box), 4.5, Id(box));
                    break;
                case ComboBox { SelectedItem: not null } combo:
                    Meets(combo.Foreground, Backdrop(combo), 4.5, Id(combo));
                    break;
                case CheckBox check:
                    Meets(check.Foreground, Backdrop(check), 4.5, Id(check));
                    break;
                case Border { BorderThickness.Left: > 0, BorderBrush: ISolidColorBrush } border:
                    Meets(border.BorderBrush, Backdrop(border, false), 3, Id(border));
                    break;
                case Button { BorderBrush: ISolidColorBrush } button:
                    Meets(button.BorderBrush, Backdrop(button, false), 3, Id(button));
                    break;
                case ListBoxItem { IsSelected: true, Content: StackPanel labels } project:
                    var selection = Backdrop(labels);
                    var surface = Backdrop(project, false);
                    Meets(selection, surface, 3, Id(project));
                    break;
                case ProgressBar progress:
                    Meets(progress.Foreground, Backdrop(progress), 3, Id(progress));
                    break;
            }
        }

        foreach (var icon in window.GetVisualDescendants().OfType<IconPath>().Where(icon =>
            icon.IsEffectivelyVisible && icon.IsEffectivelyEnabled &&
            AutomationProperties.GetAccessibilityView(icon) == AccessibilityView.Raw))
            Meets(icon.Fill ?? icon.Stroke, Backdrop(icon), 3, "launcher icon");
    }

    private static string Id(Control control) => AutomationProperties.GetAutomationId(control) ?? control.GetType().Name;

    private static Color Solid(IBrush? brush)
    {
        var solid = Assert.IsAssignableFrom<ISolidColorBrush>(brush);
        Assert.Equal(byte.MaxValue, solid.Color.A);
        Assert.Equal(1, solid.Opacity);
        return solid.Color;
    }

    private static Color Backdrop(Visual visual, bool includeSelf = true)
    {
        var ancestors = includeSelf ? visual.GetVisualAncestors().Prepend(visual) : visual.GetVisualAncestors();
        var background = Colors.Transparent;
        foreach (var ancestor in ancestors.Reverse())
        {
            var brush = ancestor switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                TemplatedControl control => control.Background,
                _ => null
            };
            if (brush is ISolidColorBrush solid) background = Composite(solid, background);
        }
        Assert.Equal(byte.MaxValue, background.A);
        return background;
    }

    private static Color Composite(ISolidColorBrush brush, Color background)
    {
        var foreground = brush.Color;
        var alpha = foreground.A / 255d * brush.Opacity;
        if (alpha == 0) return background;
        var remaining = background.A / 255d * (1 - alpha);
        var total = alpha + remaining;
        byte Channel(byte top, byte bottom) => (byte)Math.Round((top * alpha + bottom * remaining) / total);
        return Color.FromArgb((byte)Math.Round(total * 255),
            Channel(foreground.R, background.R), Channel(foreground.G, background.G), Channel(foreground.B, background.B));
    }

    private static void Meets(IBrush? foreground, Color background, double minimum, string label) =>
        Meets(Composite(Assert.IsAssignableFrom<ISolidColorBrush>(foreground), background), background, minimum, label);

    private static void Meets(Color foreground, Color background, double minimum, string? label = null)
    {
        var ratio = LauncherVisualTokens.ContrastRatio(foreground, background);
        Assert.True(ratio >= minimum, $"{label}: {foreground} on {background} is {ratio:F3}:1, expected at least {minimum}:1.");
    }
}
