using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class Prod17LayoutMatrixTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double Tolerance = 1;
    private const string ProjectName =
        "포스코DX 디지털 트윈 통합 운영 시뮬레이터 — 제철 공정·설비 상태·안전 점검 및 원격 협업 시험 프로젝트";
    private static readonly string Platform = OperatingSystem.IsWindows() ? "windows-x64" : "linux-x64";

    public static IEnumerable<object[]> Cases =>
        from size in new[] { (1280, 720), (1366, 768), (1612, 816), (1920, 1080) }
        from textScale in new[] { 1d, 1.5, 2d }
        from screen in new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "D1", "D2", "D3" }
        select new object[] { size.Item1, size.Item2, textScale, screen };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void RequiredContentFitsOrCanBeScrolledIntoView(int width, int height, double textScale, string screen)
    {
        var developer = screen.StartsWith('D');
        var versions = Enumerable.Range(1, 20).Select(number => new CatalogReleaseOption
        {
            ProjectId = "demo", Environment = "prod", Channel = "stable", Platform = Platform,
            Version = $"1.0.{number}", IsLatest = number == 2,
            Notes = "## 변경 내역\n" + string.Join("\n", Enumerable.Repeat("- 설비 운전 화면과 점검 절차를 개선했습니다.", 14))
        }).ToArray();
        var model = new LauncherDashboardViewModel(developer ? LauncherEdition.Developer : LauncherEdition.General)
        {
            Config = new()
            {
                ProjectId = "demo", DeploymentMode = "managed-agent",
                VersionPolicy = developer ? "exact" : "latest", RequestedVersion = developer ? "1.0.2" : null,
                Projects = screen == "S3"
                    ? [new() { ProjectId = "demo", DisplayName = ProjectName }, new() { ProjectId = "yard", DisplayName = "물류 관제" }]
                    : [new() { ProjectId = "demo", DisplayName = ProjectName }]
            },
            Catalog = new() { SelectionPolicy = CatalogRecommendation.ExplicitPolicy, Releases = [.. versions] },
            GeneralState = GeneralLauncherState.NotInstalled
        };
        var window = new MainWindow(new("/nonexistent/prod17-layout.json", LauncherConfigSource.Explicit, false),
            model, new(textScale, false), false) { Width = width, Height = height };
        using var cancellation = new CancellationTokenSource();
        window.Show();
        try
        {
            Assert.Equal(new Size(width, height), window.ClientSize);
            var operation = (LauncherOperationPresentation)Field(window, "_presentation").GetValue(window)!;
            switch (screen)
            {
                case "S1":
                    Check((Window)Call(window, "ShowFirstRunGuide", window)!, ["guide-start"], ["dialog-body"]);
                    break;
                case "S2":
                    Check(window, ["settings", "primary-action", "status-check", "project-install-status", "operation-status"], ["workspace-scroll"]);
                    break;
                case "S3":
                    Field(window, "_hasAuthorizedCatalog").SetValue(window, true);
                    Call(window, "BuildEnterprise");
                    Check(window, ["card-search", "card-refresh", "card-open-demo", "card-action-demo",
                        "card-open-yard", "card-action-yard"], ["project-cards"]);
                    break;
                case "S4":
                    model.Running = true;
                    operation.Begin(LauncherUiOperation.Update);
                    operation.Stage("Download");
                    operation.Percent = 42;
                    operation.Title = "다운로드 1.2 GB / 2.8 GB · 47 MB/s · 남은 시간 약 34초";
                    Call(window, "StartCancellableUiOperation", cancellation);
                    Check(window, ["cancel-operation", "operation-status", "operation-progress", "cancel-guidance"]);
                    break;
                case "S5":
                    model.GeneralState = GeneralLauncherState.RecoverableError;
                    operation.ErrorCode = "forbidden";
                    operation.SupportId = new string('f', 32);
                    operation.Title = "이 PC는 이 프로그램을 받을 권한이 없습니다.";
                    operation.Retry = new(LauncherUiOperation.Check, "demo", "prod", "stable", null);
                    Call(window, "BuildEnterprise");
                    Expand(window, "error-details");
                    Check(window, ["retry-operation", "operation-status", "copy-support-info", "safe-error-details",
                        "safe-support-info", "save-error-logs"]);
                    break;
                case "S6":
                    var settings = (Window)Call(window, "ShowGeneralSettings")!;
                    Dispatcher.UIThread.RunJobs();
                    var tabs = Find<TabControl>(settings, "settings-tabs");
                    string[][] tabActions = [[], ["settings-copy-support-info"], ["text-scale", "high-contrast",
                        "settings-first-run-guide"], ["settings-readiness", "settings-log-folder", "settings-reload"]];
                    for (var tab = 0; tab < tabActions.Length; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        Check(settings, ["dialog-cancel", "settings-export", .. (tab == 2 ? new[] { "dialog-confirm" } : []), .. tabActions[tab]],
                            ["dialog-body"]);
                    }
                    break;
                case "S7":
                    Expand(window, "release-help");
                    Expand(window, "previous-release-notes");
                    Expand(window, "previous-release-1.0.1");
                    Check(window, ["primary-action"], ["current-release-note", "previous-release-note-1.0.1"]);
                    break;
                case "S8":
                    model.ProjectStatus = new(false, null, "1.0.2", true, 0, 0, false,
                        new(new("demo", "prod", "stable", Platform, "2.0.0"), "2026-11-26T00:00:00Z"));
                    Field(window, "_checkedRecommendation").SetValue(window,
                        RecommendationChange.Find("latest", versions[1], model.ProjectStatus));
                    Call(window, "OfferRecommendationChange");
                    Check(Assert.Single(window.OwnedWindows), ["recommendation-later", "recommendation-change"], ["dialog-body"]);
                    break;
                case "D1":
                    Check(window, ["filter-환경", "filter-채널", "filter-버전 정책", "filter-요청 버전",
                        "primary-action", "update", "status-check", "open-folder"], ["developer-release-filters",
                        "release-info", "developer-installation"]);
                    break;
                case "D2":
                    Dispatcher.UIThread.RunJobs();
                    var list = Find<ListBox>(window, "developer-version-list");
                    Assert.Equal(20, list.Items.Count);
                    list.BringIntoView();
                    Dispatcher.UIThread.RunJobs();
                    var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                    Check(window, [row], Labels(row));
                    Assert.True(row.Focus(NavigationMethod.Tab));
                    for (var index = 1; index < list.Items.Count; index++)
                    {
                        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                        Dispatcher.UIThread.RunJobs();
                    }
                    Assert.Equal("1.0.20", model.Config.RequestedVersion);
                    list = Find<ListBox>(window, "developer-version-list");
                    var finalRow = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(list.Items.Count - 1));
                    Assert.Equal("developer-version-1.0.20", AutomationProperties.GetAutomationId(finalRow));
                    Assert.Same(finalRow, window.FocusManager!.GetFocusedElement());
                    Check(window, [finalRow], Labels(finalRow));
                    break;
                case "D3":
                    Call(window, "RecordDeveloperHistory", "상태 확인", "확인", LauncherHistoryResult.Failed);
                    Expand(window, "developer-history-original");
                    Check(window, ["developer-history-errors-only", "developer-history-summary",
                        "developer-history-copy", "developer-history-export", "operation-log"], ["developer-history"]);
                    break;
            }
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    private static void Expand(Window window, string id)
    {
        Dispatcher.UIThread.RunJobs();
        Find<Expander>(window, id).IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(Window window, string[] ids, string[]? regions = null)
    {
        Dispatcher.UIThread.RunJobs();
        var named = ids.Select(id => Find<Control>(window, id));
        var labels = (regions ?? []).SelectMany(id => Labels(Find<Control>(window, id)));
        Check(window, named, labels);
    }

    private static IEnumerable<TextBlock> Labels(Control region) =>
        region.GetVisualDescendants().OfType<TextBlock>().Where(IsRenderedText);

    private static void Check(Window window, IEnumerable<Control> named, IEnumerable<TextBlock> labels)
    {
        var targets = named.Concat(labels).Distinct().ToArray();
        Assert.NotEmpty(targets);
        foreach (var target in targets)
        {
            var label = Describe(target);
            Assert.True(target.IsEffectivelyVisible && target.Bounds.Width > 0 && target.Bounds.Height > 0, label);
            if (target is Button)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(target)), label);
                foreach (var caption in target.GetVisualDescendants().OfType<TextBlock>().Where(IsRenderedText))
                    AssertTextFits(caption);
            }
            if (target is TextBlock text) AssertTextFits(text);
            AssertCanShow(window, target);
        }

        // Project each rectangle to its shared ancestor before comparing. Scrolling can
        // move independent panels, so don't compare content in different scroll surfaces.
        for (var index = 0; index < targets.Length; index++)
        foreach (var other in targets.Skip(index + 1))
        {
            var target = targets[index];
            var ancestors = target.GetVisualAncestors().ToArray();
            var otherAncestors = other.GetVisualAncestors().ToArray();
            if (ancestors.Contains(other) || otherAncestors.Contains(target) ||
                ancestors.OfType<ScrollContentPresenter>().FirstOrDefault() !=
                otherAncestors.OfType<ScrollContentPresenter>().FirstOrDefault()) continue;
            var shared = ancestors.First(otherAncestors.Contains);
            var overlap = BoundsIn(target, shared).Intersect(BoundsIn(other, shared));
            Assert.True(overlap.Width <= Tolerance || overlap.Height <= Tolerance,
                $"{Describe(target)} overlaps {Describe(other)} by {overlap.Size}.");
        }
    }

    private static bool IsRenderedText(TextBlock text) =>
        text.IsEffectivelyVisible && (!string.IsNullOrWhiteSpace(text.Text) || text.Inlines?.Count > 0);

    private static void AssertTextFits(TextBlock text)
    {
        var layout = text.TextLayout;
        if (text.TextTrimming == TextTrimming.None)
            Assert.All(layout.TextLines, line => Assert.True(line.Width <= text.Bounds.Width + Tolerance,
                $"{Describe(text)} needs {line.Width} DIP of width; it has {text.Bounds.Width}."));
        Assert.True(layout.Height <= text.Bounds.Height + Tolerance,
            $"{Describe(text)} needs {layout.Height} DIP of height; it has {text.Bounds.Height}.");
    }

    private static void AssertCanShow(Window window, Control target)
    {
        var clipping = target.GetVisualAncestors().Where(visual => visual.ClipToBounds).ToArray();
        // Measure long notes a rendered line at a time. The whole note can be taller
        // than its scroll viewer, but every line must have a position inside all clips.
        var fragments = new List<Rect>();
        if (target is TextBlock text && clipping.OfType<ScrollContentPresenter>()
                .Any(clip => target.Bounds.Height > clip.Bounds.Height + Tolerance))
        {
            var y = 0d;
            foreach (var line in text.TextLayout.TextLines)
            {
                fragments.Add(new(0, y, Math.Min(line.Width, target.Bounds.Width), line.Height));
                y += line.Height;
            }
        }
        else fragments.Add(new(target.Bounds.Size));

        foreach (var fragment in fragments)
        {
            foreach (var clip in clipping.OfType<ScrollContentPresenter>())
            {
                var viewer = clip.GetVisualAncestors().OfType<ScrollViewer>().First();
                var top = target.TranslatePoint(fragment.TopLeft, clip)!.Value.Y;
                var delta = top < 0 ? top : Math.Max(0, top + fragment.Height - clip.Bounds.Height);
                viewer.Offset = new(viewer.Offset.X, viewer.Offset.Y + delta);
                Dispatcher.UIThread.RunJobs();
            }
            foreach (var clip in clipping.Append(window))
            {
                var point = target.TranslatePoint(fragment.TopLeft, clip)!.Value;
                var within = new Rect(point, fragment.Size);
                var viewport = clip == window ? window.ClientSize : clip.Bounds.Size;
                Assert.True(within.Left >= -Tolerance && within.Top >= -Tolerance &&
                    within.Right <= viewport.Width + Tolerance && within.Bottom <= viewport.Height + Tolerance,
                    $"{Describe(target)} bounds {within} exceed {clip.GetType().Name} {viewport}.");
            }
        }
    }

    private static Rect BoundsIn(Control control, Visual visual) =>
        new(control.TranslatePoint(default, visual)!.Value, control.Bounds.Size);

    private static string Describe(Control control) =>
        AutomationProperties.GetAutomationId(control) is { Length: > 0 } id ? id :
            control is TextBlock text ? text.Text ?? "text" : control.GetType().Name;

    private static T Find<T>(Window window, string id) where T : Control
    {
        var matches = window.GetVisualDescendants().OfType<T>()
            .Where(control => AutomationProperties.GetAutomationId(control) == id).ToArray();
        Assert.True(matches.Length == 1, $"{id}: {string.Join("; ", matches.Select(c => $"{c.GetType().Name} {c.Bounds}"))}");
        return matches[0];
    }

    private static FieldInfo Field(MainWindow window, string name) => window.GetType().GetField(name, PrivateInstance)!;

    private static object? Call(MainWindow window, string name, params object[] args)
    {
        var method = window.GetType().GetMethods(PrivateInstance).Single(method => method.Name == name &&
            method.GetParameters().Length == args.Length);
        return method.Invoke(window, args);
    }
}
