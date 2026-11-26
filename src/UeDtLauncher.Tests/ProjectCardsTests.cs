using System.Net;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class ProjectCardsTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task StartupUsesAuthorizedProjectCount(int count)
    {
        using var fixture = new Fixture();
        fixture.Allowed = new[] { "a", "b", "c" }.Take(count).ToArray();
        await fixture.Refresh();

        Assert.Equal(count < 2 ? 0 : count, fixture.Cards().Length);
        Assert.Equal(count == 0, fixture.Texts().Contains("받을 수 있는 프로그램이 없습니다."));
        Assert.Equal(count == 1, fixture.Controls().Any(control => Id(control) == "project-install-status"));
        Assert.Equal(fixture.Allowed, fixture.Backend.Checks);
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [AvaloniaFact]
    public async Task EachCardChecksIndependentlyAndOnlyShowsAvailableData()
    {
        using var fixture = new Fixture();
        fixture.Backend.FirstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = fixture.Refresh();
        await WaitFor(() => fixture.Backend.Checks.Count == 1);
        Assert.Equal(new[] { "a" }, fixture.Backend.Checks);
        Assert.All(fixture.Cards(), card => Assert.EndsWith(" — 상태 확인 중", AutomationProperties.GetName(card)));
        fixture.Backend.FirstCheck.SetResult();
        await refresh;

        Assert.Equal(new[] { "프로그램 A — 설치 필요", "프로그램 B — 최신 상태", "프로그램 C — 오류" },
            fixture.Cards().Select(AutomationProperties.GetName));
        Assert.Equal(1, fixture.Backend.PeakChecks);
        Assert.Equal(new[] { "설치", "실행", "다시 확인" },
            new[] { "a", "b", "c" }.Select(id => AutomationProperties.GetName(fixture.Find<Button>("card-action-" + id))));
        Assert.Contains(fixture.Texts(), value => value.Contains("권한"));
        Assert.DoesNotContain(fixture.Texts(), value => new[] { "공개일", "패키지 크기", "업데이트 크기", "미승격",
            "회수 원인", "설치 목록", "사용량", "서버 주소", "서비스 버전", "secret-marker" }.Any(value.Contains));
        Click(fixture.Find<Button>("card-action-c"));
        await WaitFor(() => fixture.Backend.Checks.Count == 6 && !fixture.Model.Running);
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [AvaloniaFact]
    public async Task ActiveCardBlocksOtherActionsUntilItFinishes()
    {
        using var fixture = new Fixture();
        fixture.Backend.CompleteExecution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Refresh();
        Click(fixture.Find<Button>("card-action-a"));
        await WaitFor(() => fixture.Backend.Executions == 1);
        Assert.Equal("a", fixture.Backend.ExecutedProject);
        Assert.False(fixture.Find<Button>("card-action-b").IsEnabled);
        Assert.Contains("다른 프로그램", fixture.Find<TextBlock>("card-busy-guidance").Text);
        Click(fixture.Find<Button>("card-action-b"));
        Assert.Equal(1, fixture.Backend.Executions);
        fixture.Backend.CompleteExecution.SetResult();
        await WaitFor(() => !fixture.Model.Running);
        Assert.True(fixture.Find<Button>("card-action-b").IsEnabled);
        Assert.False(fixture.Find<Button>("card-action-a").IsEnabled);
        Assert.Equal("프로그램 A — 실행 중", AutomationProperties.GetName(fixture.Find<Border>("program-card-a")));
    }

    [AvaloniaTheory]
    [InlineData(RuntimeState.Running, "실행 중")]
    [InlineData(RuntimeState.LaunchPending, "실행 상태 확인 필요")]
    [InlineData(RuntimeState.Unknown, "실행 상태 확인 필요")]
    [InlineData(null, "실행 상태 확인 필요")]
    public async Task RuntimeUncertaintyDisablesOnlyItsCard(RuntimeState? state, string label)
    {
        using var fixture = new Fixture();
        fixture.Backend.Runtime = state is { } observed ? new(observed, "fixture", "fixture") : null;
        await fixture.Refresh();
        Assert.False(fixture.Find<Button>("card-action-a").IsEnabled);
        Assert.True(fixture.Find<Button>("card-action-b").IsEnabled);
        Assert.Equal("프로그램 A — " + label, AutomationProperties.GetName(fixture.Find<Border>("program-card-a")));
    }

    [AvaloniaFact]
    public async Task NavigationKeepsSelectionAndRefreshRemovesRevokedProject()
    {
        using var fixture = new Fixture();
        await fixture.Refresh();
        Click(fixture.Find<Button>("card-open-b"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal("b", fixture.Model.SelectedProject.ProjectId);
        Assert.Equal("최신 상태", fixture.Find<TextBlock>("project-install-status").Text);
        Assert.Empty(fixture.Cards());
        Click(fixture.Find<Button>("project-cards-back"));
        await fixture.Refresh();
        Assert.Equal("b", fixture.Model.SelectedProject.ProjectId);
        Assert.Equal(3, fixture.Cards().Length);

        var search = fixture.Find<TextBox>("card-search");
        search.Text = "프로그램 A";
        Dispatcher.UIThread.RunJobs();
        Assert.Single(fixture.Cards());
        Assert.Equal("b", fixture.Model.SelectedProject.ProjectId);
        search.Text = "없음";
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Find<TextBlock>("card-search-empty").IsVisible);
        Assert.Empty(fixture.Cards());
        search.Text = "";
        Dispatcher.UIThread.RunJobs();
        Click(fixture.Find<Button>("card-open-b"));
        await WaitFor(() => !fixture.Model.Running);
        fixture.Allowed = ["a", "c"];
        await fixture.Refresh();
        Assert.Equal(2, fixture.Cards().Length);
        Assert.Equal("a", fixture.Model.SelectedProject.ProjectId);
        Assert.Equal("받을 수 있는 프로그램 목록이 변경되었습니다.", fixture.Find<TextBlock>("card-permission-notice").Text);
        Assert.DoesNotContain(fixture.Controls(), control => Id(control) == "program-card-b");
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [AvaloniaFact]
    public async Task ArrowsTabAndEnterReachCardsAndActions()
    {
        using var fixture = new Fixture();
        await fixture.Refresh();
        fixture.Window.Activate();
        Assert.True(fixture.Find<Button>("card-open-a").Focus());
        Press(fixture.Window, PhysicalKey.ArrowRight);
        Assert.True(fixture.Find<Button>("card-open-b").IsFocused);
        Assert.Equal("프로그램 B — 최신 상태", AutomationProperties.GetName(fixture.Find<Button>("card-open-b")));
        Press(fixture.Window, PhysicalKey.Tab);
        Assert.True(fixture.Find<Button>("card-action-b").IsFocused);
        Assert.True(fixture.Find<Button>("card-open-b").Focus());
        Press(fixture.Window, PhysicalKey.Enter);
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal("b", fixture.Model.SelectedProject.ProjectId);
        Assert.True(fixture.Find<Button>("project-cards-back").IsFocused);
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [AvaloniaFact]
    public async Task DetailF6ChecksOnlyTheSelectedProject()
    {
        using var fixture = new Fixture();
        await fixture.Refresh();
        Click(fixture.Find<Button>("card-open-b"));
        await WaitFor(() => !fixture.Model.Running);
        fixture.Backend.Checks.Clear();
        fixture.Window.Activate();
        Assert.True(fixture.Find<Button>("status-check").Focus());
        Press(fixture.Window, PhysicalKey.F6);
        await WaitFor(() => fixture.Backend.Checks.Count > 0 && !fixture.Model.Running);
        Assert.Equal(new[] { "b" }, fixture.Backend.Checks);
        Assert.Empty(fixture.Cards());
    }

    [AvaloniaFact]
    public async Task SequentialResultsCreateOneCardPerProjectAndUpdateTheExistingCard()
    {
        using var fixture = new Fixture();
        for (var i = 1; i <= 3; i++)
            fixture.Backend.CheckBarriers[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new HashSet<Border>(ReferenceEqualityComparer.Instance);
        var refresh = fixture.Initialize();
        await WaitFor(() => fixture.Backend.Checks.Count == 1);
        var layout = fixture.Window.Content;
        for (var i = 1; i <= 3; i++)
        {
            created.UnionWith(fixture.Cards());
            fixture.Backend.CheckBarriers[i].SetResult();
            var next = i + 1;
            if (i < 3) await WaitFor(() => fixture.Backend.Checks.Count == next);
            Assert.Same(layout, fixture.Window.Content);
        }
        await refresh;
        Assert.Same(layout, fixture.Window.Content);
        created.UnionWith(fixture.Cards());
        Assert.Equal(3, created.Count);
        var initialCards = fixture.Cards();

        fixture.Backend.Runtime = new(RuntimeState.Running, "fixture", "fixture");
        await fixture.Refresh();
        created.UnionWith(fixture.Cards());
        Assert.Equal(3, created.Count);
        Assert.Equal(initialCards, fixture.Cards());
        Assert.Equal("프로그램 A — 실행 중", AutomationProperties.GetName(initialCards[0]));
    }

    [AvaloniaFact]
    public async Task VerticalArrowsFollowWrappedRowsAfterSameBucketResize()
    {
        using var fixture = new Fixture();
        fixture.Window.Width = 1060;
        Dispatcher.UIThread.RunJobs();
        await fixture.Refresh();
        fixture.Window.Activate();
        var cards = fixture.Cards();
        Assert.Equal(cards[0].Bounds.Top, cards[2].Bounds.Top);

        fixture.Window.Width = 960;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(cards, fixture.Cards());
        Assert.Equal(cards[0].Bounds.Top, cards[1].Bounds.Top);
        Assert.True(cards[2].Bounds.Top > cards[0].Bounds.Top);
        Assert.True(fixture.Find<Button>("card-open-a").Focus());
        Press(fixture.Window, PhysicalKey.ArrowDown);
        Assert.True(fixture.Find<Button>("card-open-c").IsFocused);
        Press(fixture.Window, PhysicalKey.ArrowUp);
        Assert.True(fixture.Find<Button>("card-open-a").IsFocused);
    }

    [AvaloniaFact]
    public async Task NarrowIdleCardViewKeepsTheServiceStatusInTheHeader()
    {
        using var fixture = new Fixture();
        fixture.Window.Width = 750;
        Dispatcher.UIThread.RunJobs();
        await fixture.Refresh();
        var service = fixture.Find<TextBlock>("service-status");
        Assert.True(service.IsEffectivelyVisible);
        var header = fixture.Find<Image>("brand-logo").GetLogicalAncestors().OfType<Grid>().First();
        Assert.Contains(header, service.GetLogicalAncestors());
    }

    [AvaloniaFact]
    public async Task CancelledCardShowsTheOperationOutcomeAndOffersAReadOnlyCheck()
    {
        using var fixture = new Fixture();
        fixture.Backend.CompleteExecution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Refresh();
        Click(fixture.Find<Button>("card-action-a"));
        await WaitFor(() => fixture.Backend.Executions == 1);
        Click(fixture.Find<Button>("cancel-operation"));
        await WaitFor(() => !fixture.Model.Running);

        Assert.Equal("프로그램 A — 작업 취소 완료", AutomationProperties.GetName(fixture.Find<Button>("card-open-a")));
        Assert.Contains("작업 취소 완료 · 다시 확인해 주세요.", fixture.Texts());
        Assert.Equal("다시 확인", AutomationProperties.GetName(fixture.Find<Button>("card-action-a")));
        Click(fixture.Find<Button>("card-action-a"));
        await WaitFor(() => fixture.Backend.Checks.Count == 7 && !fixture.Model.Running);
        Assert.Equal(1, fixture.Backend.Executions);
    }

    private static string? Id(Control control) => AutomationProperties.GetAutomationId(control);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly MethodInfo RefreshMethod = typeof(MainWindow).GetMethod(
            "RefreshSelectionStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "uedt-project-cards-" + Guid.NewGuid().ToString("N"));
        public string[] Allowed { get; set; } = ["a", "b", "c"];
        public LauncherDashboardViewModel Model { get; }
        public MainWindow Window { get; }
        public Backend Backend { get; } = new();

        public Fixture()
        {
            var path = Path.Combine(_root, "general.json");
            Directory.CreateDirectory(_root);
            File.WriteAllText(path, "{}");
            LauncherConfig Config() => new()
            {
                DeploymentMode = "portable", DistributionServerUrl = "https://fixture.invalid", ProjectId = "a",
                InstallDir = Path.Combine(_root, "apps"), LogDir = Path.Combine(_root, "logs"),
                StateRootDir = Path.Combine(_root, "state"), Projects = [new() { ProjectId = "a", DisplayName = "프로그램 A" }]
            };
            Model = new(LauncherEdition.General) { Config = Config() };
            Window = new(new(path, LauncherConfigSource.Explicit, false), Model, new(), false, Backend,
                (_, platform, _) => Task.FromResult(new CatalogSnapshot
                {
                    Projects = Allowed.Select(id => new CatalogProjectOption { ProjectId = id, DisplayName = "프로그램 " + id.ToUpperInvariant() }).ToList(),
                    Releases = Allowed.Select(id => new CatalogReleaseOption
                    {
                        ProjectId = id, Version = "1.0.0", Environment = "prod", Channel = "stable", Platform = platform, IsLatest = true
                    }).ToList()
                }), runtimeConfigLoader: _ => Task.FromResult(Config()));
            Window.Show();
        }

        public async Task Refresh()
        {
            await (Task)RefreshMethod.Invoke(Window, [false])!;
            Dispatcher.UIThread.RunJobs();
        }

        public async Task Initialize()
        {
            var startup = typeof(MainWindow).GetMethod("InitializeStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)startup.Invoke(Window, null)!;
            Dispatcher.UIThread.RunJobs();
        }

        public IEnumerable<Control> Controls() => Window.GetLogicalDescendants().OfType<Control>();
        public Border[] Cards() => Controls().OfType<Border>().Where(control => Id(control)?.StartsWith("program-card-", StringComparison.Ordinal) == true).ToArray();
        public T Find<T>(string id) where T : Control => Controls().OfType<T>().Single(control => Id(control) == id);
        public IEnumerable<string> Texts() => Controls().OfType<TextBlock>().Select(text => text.Text ?? "");

        public void Dispose()
        {
            Window.Close();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Backend : ILauncherUiBackend
    {
        public List<string> Checks { get; } = [];
        public TaskCompletionSource? FirstCheck { get; set; }
        public Dictionary<int, TaskCompletionSource> CheckBarriers { get; } = new();
        public TaskCompletionSource? CompleteExecution { get; set; }
        public RuntimeObservation? Runtime { get; set; } = new(RuntimeState.Quiescent, "fixture", "fixture");
        private int _concurrentChecks;
        public int PeakChecks { get; private set; }
        public int Executions { get; private set; }
        public string? ExecutedProject { get; private set; }

        public async Task<LauncherUiOperationResult> CheckAsync(LauncherUiOperationContext context, LauncherConfig config,
            Action<LauncherProgress> progress, CancellationToken token = default)
        {
            Assert.Equal(context.ProjectId, config.ProjectId);
            Checks.Add(context.ProjectId);
            PeakChecks = Math.Max(PeakChecks, ++_concurrentChecks);
            try
            {
                if (Checks.Count == 1 && FirstCheck is { } barrier) await barrier.Task.WaitAsync(token);
                if (CheckBarriers.TryGetValue(Checks.Count, out var checkpoint)) await checkpoint.Task.WaitAsync(token);
                if (context.ProjectId == "c") throw new HttpRequestException("secret-marker", null, HttpStatusCode.Forbidden);
                VersionedReleasePaths.Bind(config, context.Selection!);
                var installed = context.ProjectId == "b";
                return new(config, context.Selection, new(installed, installed ? "1.0.0" : null, "1.0.0", !installed, 0, 0, false),
                    installed ? new(RuntimeState.Quiescent, "fixture", "fixture") : Runtime);
            }
            finally { _concurrentChecks--; }
        }

        public async Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context, LauncherConfig config,
            bool repair, bool launch, Action<LauncherProgress> progress, FileLogger? logger, CancellationToken token = default)
        {
            Executions++;
            ExecutedProject = context.ProjectId;
            Assert.True(launch);
            Assert.False(repair);
            if (CompleteExecution is { } barrier) await barrier.Task.WaitAsync(token);
            VersionedReleasePaths.Bind(config, context.Selection!);
            return new(config, context.Selection, new(true, "1.0.0", "1.0.0", false, 0, 0, false),
                new(RuntimeState.Running, "fixture", "fixture"), LauncherUiCompletion.Completed);
        }
    }
}
