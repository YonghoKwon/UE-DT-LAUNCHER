using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public class ProductionDashboardVisualTests
{
    [AvaloniaTheory]
    [InlineData(null, false)]
    [InlineData("corrupt", false)]
    [InlineData("oversized", false)]
    [InlineData(null, true)]
    public void DashboardRendersLegibleProjectNameTile(string? image, bool highContrast)
    {
        using var fixture = new Fixture();
        var model = fixture.Model();
        var imagePath = Path.Combine(fixture.Root, "hero.png");
        if (image == "corrupt")
        {
            // Pass the signature check and exercise the actual bitmap decode fallback.
            File.WriteAllBytes(imagePath, [137, 80, 78, 71, 13, 10, 26, 10, 0]);
            Assert.True(ProjectVisualResolver.Resolve(new ProjectUiConfig { HeroPath = imagePath }, fixture.ConfigPath, ProjectVisualKind.Hero).HasImage);
        }
        else if (image == "oversized")
        {
            using var stream = File.Create(imagePath);
            stream.SetLength(ProjectVisualResolver.MaxAssetBytes + 1);
        }
        model.Config.Projects[0].HeroPath = image is null ? null : imagePath;
        var window = fixture.Window(model, highContrast: highContrast, scale: 1.25);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var initials = Find<TextBlock>(window, "project-name-initials");
            Assert.Equal("제D", initials.Text);
            Assert.Equal(LauncherVisualTokens.FontProject * 1.25, initials.FontSize);
            Assert.True(initials.IsEffectivelyVisible);
            Assert.True(initials.Bounds.Width > 0 && initials.Bounds.Height > 0);
            var tile = Assert.IsType<Border>(initials.Parent);
            var textColor = Assert.IsAssignableFrom<ISolidColorBrush>(initials.Foreground).Color;
            var tileColor = Assert.IsAssignableFrom<ISolidColorBrush>(tile.Background).Color;
            Assert.Equal(byte.MaxValue, textColor.A);
            Assert.True(LauncherVisualTokens.ContrastRatio(textColor, tileColor) >= 4.5);
            Assert.DoesNotContain(tile.GetVisualDescendants(), child => child is Image);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDeploymentShowsGuidanceAndReadOnlyAction(bool developer)
    {
        using var fixture = new Fixture();
        var model = fixture.Model(developer);
        model.Config.DistributionServerUrl = "https://fixture.invalid";
        var backend = new DelayedCheck();
        var window = fixture.Window(model, backend,
            (_, _, _) => Task.FromResult(new CatalogSnapshot { Status = "확인 완료 · 배포 0건" }));
        window.Show();
        try
        {
            await Initialize(window);
            var panel = Find<Border>(window, "deployment-empty");
            Assert.True(panel.IsEffectivelyVisible);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(),
                label => label.Text == "아직 공개된 버전이 없습니다. 담당자가 공개하면 표시됩니다.");
            var actions = Find<WrapPanel>(panel, "deployment-empty-actions");
            Assert.True(Find<Button>(actions, "empty-refresh").IsEnabled);
            Assert.False(backend.Started.Task.IsCompleted);
            Assert.Null(model.ProjectStatus);
            Assert.Equal(GeneralLauncherState.RecoverableError, model.GeneralState);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptySearchShowsClearActionAndRestoresListWithoutChangingSelectedProject()
    {
        using var fixture = new Fixture();
        var model = fixture.Model(developer: true);
        var window = fixture.Window(model);
        window.Show();
        try
        {
            var search = Find<TextBox>(window, "project-search");
            var panel = Find<Border>(window, "project-search-empty");
            Assert.False(panel.IsVisible);
            search.Text = "일치하지 않는 검색어";
            Dispatcher.UIThread.RunJobs();

            Assert.True(panel.IsEffectivelyVisible);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), label => label.Text == "검색 결과가 없습니다");
            Assert.False(Find<ListBox>(window, "project-list").IsVisible);
            var actions = Find<WrapPanel>(panel, "project-search-empty-actions");
            var clear = Find<Button>(actions, "clear-project-search");
            Assert.True(clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, search.Text);
            Assert.True(search.IsFocused);
            Assert.False(panel.IsVisible);
            Assert.True(Find<ListBox>(window, "project-list").IsEffectivelyVisible);
            Assert.Equal("demo", model.SelectedProject.ProjectId);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task FirstCheckShowsSkeletonUntilExistingStatusResultIsApplied(bool developer, bool highContrast)
    {
        using var fixture = new Fixture();
        var model = fixture.Model(developer);
        var catalog = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DelayedCheck();
        var window = fixture.Window(model, backend, (_, _, _) => catalog.Task, highContrast);
        window.Show();
        try
        {
            var startup = Initialize(window);
            var skeleton = Find<Border>(window, "startup-skeleton");
            Assert.True(skeleton.IsEffectivelyVisible);
            Assert.Equal(3, Assert.IsType<StackPanel>(skeleton.Child).Children.Count);
            Assert.Equal(GeneralLauncherState.Checking, model.GeneralState);
            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Control>(), c => Id(c) == "project-install-status");

            catalog.SetResult(new CatalogSnapshot());
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Find<Border>(window, "startup-skeleton").IsEffectivelyVisible);
            backend.Completed.SetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Control>(), c => Id(c) == "startup-skeleton");
            Assert.Equal(GeneralLauncherState.Ready, model.GeneralState);
            Assert.Equal(PrimaryActionKind.Launch, model.PrimaryAction);
            var state = Find<TextBlock>(window, "project-install-status");
            Assert.Equal("최신 상태", state.Text);
            Assert.Equal(LauncherVisualTokens.StatusColor(LauncherStatusTone.Normal, developer, highContrast),
                Assert.IsAssignableFrom<ISolidColorBrush>(state.Foreground).Color);
            Assert.True(Find<Button>(window, "primary-action").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedFirstCatalogCheckReplacesSkeletonWithExistingRecoveryState()
    {
        using var fixture = new Fixture();
        var model = fixture.Model();
        var catalog = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = fixture.Window(model, catalog: (_, _, _) => catalog.Task);
        window.Show();
        try
        {
            var startup = Initialize(window);
            Assert.True(Find<Border>(window, "startup-skeleton").IsEffectivelyVisible);
            catalog.SetException(new HttpRequestException("isolated fixture"));
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(window.GetLogicalDescendants().OfType<Control>(), c => Id(c) == "startup-skeleton");
            Assert.Equal(GeneralLauncherState.RecoverableError, model.GeneralState);
            Assert.Equal(PrimaryActionKind.RetryCheck, model.PrimaryAction);
            Assert.Equal("오류", Find<TextBlock>(window, "project-install-status").Text);
        }
        finally { window.Close(); }
    }

    private static T Find<T>(Control control, string id) where T : Control =>
        control.GetLogicalDescendants().OfType<T>().Single(child => Id(child) == id);

    private static string? Id(Control control) => AutomationProperties.GetAutomationId(control);

    private static Task Initialize(MainWindow window) =>
        (Task)typeof(MainWindow).GetMethod("InitializeStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null)!;

    private sealed class DelayedCheck : ILauncherUiBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context, LauncherConfig config,
            Action<LauncherProgress> progress, CancellationToken token = default)
        {
            Started.SetResult();
            await Completed.Task.WaitAsync(token);
            return new(config, context.Selection, new(true, "1.0.0", "1.0.0", false, 0, 0, false),
                new(RuntimeState.Quiescent, "stopped", "stopped"));
        }

        public Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context, LauncherConfig config,
            bool repair, bool launch, Action<LauncherProgress> progress, FileLogger? logger, CancellationToken token = default) =>
            throw new InvalidOperationException("The visual fixture only checks status.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "uedt-dashboard-visual-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath => Path.Combine(Root, "launcher.config.json");

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ConfigPath, "{}");
        }

        public LauncherDashboardViewModel Model(bool developer = false) =>
            new(developer ? LauncherEdition.Developer : LauncherEdition.General) { Config = Config() };

        public MainWindow Window(LauncherDashboardViewModel model, ILauncherUiBackend? backend = null,
            Func<LauncherConfig, string, CancellationToken, Task<CatalogSnapshot>>? catalog = null,
            bool highContrast = false, double scale = 1) =>
            new(new(ConfigPath, LauncherConfigSource.Explicit, false), model, new(scale, highContrast), false,
                backend, catalog, runtimeConfigLoader: _ => Task.FromResult(Config()));

        private LauncherConfig Config() => new()
        {
            DeploymentMode = "portable",
            ProjectId = "demo",
            InstallDir = Path.Combine(Root, "app"),
            StateRootDir = Path.Combine(Root, "state"),
            LogDir = Path.Combine(Root, "logs"),
            Projects = [new() { ProjectId = "demo", DisplayName = "제철소 DT" }]
        };

        public void Dispose() => Directory.Delete(Root, true);
    }
}
