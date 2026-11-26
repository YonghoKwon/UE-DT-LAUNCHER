using System.ComponentModel;
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

        var beforeRefresh = fixture.Backend.Checks.Count;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRunningChanged(object? sender, PropertyChangedEventArgs args)
        {
            // Catalog refresh also becomes idle, before the card status checks start.
            if (args.PropertyName == nameof(LauncherDashboardViewModel.Running) && !fixture.Model.Running &&
                fixture.Backend.Checks.Count > beforeRefresh)
                completed.TrySetResult();
        }

        fixture.Model.PropertyChanged += OnRunningChanged;
        try
        {
            Click(fixture.Find<Button>("card-action-a"));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { fixture.Model.PropertyChanged -= OnRunningChanged; }

        Assert.Equal(new[] { "a", "b", "c", "a", "a", "b", "c" }, fixture.Backend.Checks);
        Assert.False(fixture.Model.Running);
        Assert.Equal(1, fixture.Backend.Executions);
    }

    [AvaloniaTheory]
    [InlineData(false, RuntimeState.Quiescent, true, "다운로드와 설치")]
    [InlineData(true, RuntimeState.Quiescent, true, "바로 실행")]
    [InlineData(false, RuntimeState.Running, false, "프로그램이 실행 중")]
    [InlineData(false, RuntimeState.LaunchPending, false, "실행 상태를 확인")]
    [InlineData(false, RuntimeState.Unknown, false, "실행 상태를 확인")]
    [InlineData(false, null, false, "실행 상태를 확인")]
    public async Task RecommendationDialogUsesTargetStatusAndChecksPreviousRuntime(
        bool installed, RuntimeState? runtime, bool enabled, string message)
    {
        using var fixture = new Fixture { Allowed = ["a"] };
        fixture.Backend.PreviousVersion = "2.0.0";
        fixture.Backend.RecommendationInstalled = installed;
        fixture.Backend.PreviousRuntime = runtime is { } state ? new(state, "fixture", "fixture") : null;
        await fixture.Refresh();

        var dialog = Assert.Single(fixture.Window.OwnedWindows);
        var accept = dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-change");
        Assert.Equal(enabled, accept.IsEnabled);
        Assert.Equal("1.0.0로 변경", AutomationProperties.GetName(accept));
        var text = dialog.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
        Assert.Contains("프로그램 A의 권장 버전이 1.0.0으로 바뀌었습니다.", text);
        Assert.Contains("이 PC에 이전에 설치한 버전은 2.0.0입니다.", text);
        Assert.Contains(text, value => value.Contains(message));
        Assert.DoesNotContain(text.Concat(fixture.Texts()), value => new[] { "공개일", "패키지 크기", "업데이트 크기",
            "미승격", "회수 원인", "관리자가 되돌", "설치 목록", "사용량", "서버 주소", "서비스 버전", "마지막으로 쓴", "사용자 데이터" }.Any(value.Contains));
        Assert.True(fixture.Find<TextBlock>("recommendation-badge").IsVisible);
        Assert.Equal(0, fixture.Backend.Executions);
        Assert.Equal(new[] { "a" }, fixture.Backend.Checks);
        Assert.Equal("2.0.0", Assert.Single(fixture.Backend.RuntimeReads).Version);
        Click(accept);
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal(enabled ? 1 : 0, fixture.Backend.Executions);
        if (enabled)
        {
            Assert.Equal("1.0.0", fixture.Backend.ExecutedSelection?.Version);
            Assert.Equal("latest", fixture.Model.Config.VersionPolicy);
        }
    }

    [AvaloniaFact]
    public async Task LaterKeepsBothBadgesAndEachRecommendationOpensOnce()
    {
        using var fixture = new Fixture { Allowed = ["a", "b"] };
        fixture.Backend.PreviousVersion = "3.0.0";
        await fixture.Refresh();

        Assert.True(fixture.Find<TextBlock>("card-recommendation-a").IsEffectivelyVisible);
        Assert.Empty(fixture.Window.OwnedWindows);
        Click(fixture.Find<Button>("card-action-a"));
        await WaitFor(() => fixture.Window.OwnedWindows.Count > 0 && !fixture.Model.Running);
        Dismiss();
        Assert.True(fixture.Find<TextBlock>("recommendation-badge").IsVisible);
        await fixture.Refresh();
        Assert.Empty(fixture.Window.OwnedWindows);
        Click(fixture.Find<Button>("project-cards-back"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.True(fixture.Find<TextBlock>("card-recommendation-a").IsEffectivelyVisible);
        Assert.EndsWith(" · 권장 버전 변경", AutomationProperties.GetName(fixture.Find<Button>("card-open-a")));
        Assert.EndsWith(" · 권장 버전 변경", AutomationProperties.GetName(fixture.Find<Border>("program-card-a")));
        Click(fixture.Find<Button>("card-open-a"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.Empty(fixture.Window.OwnedWindows);

        fixture.RecommendedVersion = "2.0.0";
        await fixture.Refresh();
        Dismiss();
        fixture.RecommendedVersion = "1.0.0";
        await fixture.Refresh();
        Assert.Empty(fixture.Window.OwnedWindows);
        Assert.Equal(0, fixture.Backend.Executions);

        void Dismiss()
        {
            var dialog = Assert.Single(fixture.Window.OwnedWindows);
            Click(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-later"));
            Assert.Empty(fixture.Window.OwnedWindows);
        }
    }

    [AvaloniaFact]
    public async Task RetainedRecommendationShowsChangeAndBlocksWhilePreviousVersionRuns()
    {
        using var fixture = new Fixture { Allowed = ["a"] };
        fixture.Backend.RecommendationInstalled = true;
        fixture.Backend.LastActiveVersion = "2.0.0";
        fixture.Backend.PreviousRuntime = new(RuntimeState.Running, "fixture", "이전 설치 실행 중");
        await fixture.Refresh();

        var dialog = Assert.Single(fixture.Window.OwnedWindows);
        Assert.False(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-change").IsEnabled);
        Assert.False(fixture.Find<Button>("primary-action").IsEnabled);
        Assert.Null(fixture.Model.ProjectStatus?.PreviousInstallation);
        Assert.Equal("1.0.0", fixture.Model.ProjectStatus?.InstalledVersion);
        Assert.Equal("2.0.0", Assert.Single(fixture.Backend.RuntimeReads).Version);
        Click(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-later"));
        Assert.True(fixture.Find<TextBlock>("recommendation-badge").IsVisible);
        Assert.Equal(0, fixture.Backend.Executions);
    }

    [AvaloniaFact]
    public async Task PreviousRuntimeIsRecheckedBeforeAccepting()
    {
        using var fixture = new Fixture { Allowed = ["a"] };
        fixture.Backend.PreviousVersion = "2.0.0";
        await fixture.Refresh();
        var dialog = Assert.Single(fixture.Window.OwnedWindows);
        fixture.Backend.PreviousRuntime = new(RuntimeState.Running, "fixture", "fixture");
        Click(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-change"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal(0, fixture.Backend.Executions);
        Assert.False(fixture.Find<Button>("primary-action").IsEnabled);
        Assert.Equal(new[] { "2.0.0", "2.0.0" }, fixture.Backend.RuntimeReads.Select(read => read.Version));
        Assert.Equal("1.0.0", fixture.Model.ProjectStatus?.AvailableVersion);
        Assert.Contains("이전 설치 버전", fixture.Find<TextBlock>("project-state-guidance").Text);
    }

    [AvaloniaFact]
    public async Task RunningRecommendationUsesItsOwnObservation()
    {
        using var fixture = new Fixture { Allowed = ["a"] };
        fixture.Backend.PreviousVersion = "2.0.0";
        fixture.Backend.Runtime = new(RuntimeState.Running, "fixture", "추천 버전 실행 중");
        await fixture.Refresh();
        Assert.Empty(fixture.Backend.RuntimeReads);
        Assert.Equal("추천 버전 실행 중", fixture.Find<TextBlock>("project-state-guidance").Text);
        var dialog = Assert.Single(fixture.Window.OwnedWindows);
        Assert.False(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-change").IsEnabled);
    }

    [AvaloniaFact]
    public async Task ContinuingCancelledRecommendationWaitsForPreviousVersionToStop()
    {
        using var fixture = new Fixture { Allowed = ["a"] };
        fixture.Backend.PreviousVersion = "2.0.0";
        fixture.Backend.CompleteExecution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.RetainCancelledDownload = true;
        await fixture.Refresh();
        var dialog = Assert.Single(fixture.Window.OwnedWindows);
        Click(dialog.GetLogicalDescendants().OfType<Button>().Single(control => Id(control) == "recommendation-change"));
        await WaitFor(() => fixture.Backend.Executions == 1);
        Click(fixture.Find<Button>("cancel-operation"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal("계속", AutomationProperties.GetName(fixture.Find<Button>("resume-operation")));

        fixture.Backend.PreviousRuntime = new(RuntimeState.Running, "fixture", "이전 설치 실행 중");
        Click(fixture.Find<Button>("resume-operation"));
        await WaitFor(() => !fixture.Model.Running);
        Assert.Equal(1, fixture.Backend.Executions);
        Assert.Equal(3, fixture.Backend.RuntimeReads.Count);
        Assert.Equal(GeneralLauncherState.RuntimeBlocked, fixture.Model.GeneralState);
        Assert.Contains("이전 설치 버전이 실행 중", fixture.Find<TextBlock>("project-state-guidance").Text);
        Assert.DoesNotContain(fixture.Controls(), control => Id(control) == "resume-operation");

        fixture.Backend.PreviousRuntime = new(RuntimeState.Quiescent, "fixture", "이전 설치 종료");
        fixture.Backend.CompleteExecution = null;
        await fixture.Refresh();
        Click(fixture.Find<Button>("resume-operation"));
        await WaitFor(() => fixture.Backend.Executions == 2 && !fixture.Model.Running);
        Assert.Equal("1.0.0", fixture.Backend.ExecutedSelection?.Version);
        Assert.NotNull(fixture.Backend.ResumedOperation);
        Assert.Equal(5, fixture.Backend.RuntimeReads.Count);
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
        public string RecommendedVersion { get; set; } = "1.0.0";
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
                        ProjectId = id, Version = RecommendedVersion, Environment = "prod", Channel = "stable", Platform = platform, IsLatest = true
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
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Backend : ILauncherUiBackend
    {
        public List<string> Checks { get; } = [];
        public List<ReleaseSelection> RuntimeReads { get; } = [];
        public TaskCompletionSource? FirstCheck { get; set; }
        public Dictionary<int, TaskCompletionSource> CheckBarriers { get; } = new();
        public TaskCompletionSource? CompleteExecution { get; set; }
        public RuntimeObservation? Runtime { get; set; } = new(RuntimeState.Quiescent, "fixture", "fixture");
        public RuntimeObservation? PreviousRuntime { get; set; } = new(RuntimeState.Quiescent, "fixture", "fixture");
        public string? PreviousVersion { get; set; }
        public string? LastActiveVersion { get; set; }
        public bool RecommendationInstalled { get; set; }
        public bool RetainCancelledDownload { get; set; }
        public OperationStatus? ResumedOperation { get; private set; }
        private int _concurrentChecks;
        public int PeakChecks { get; private set; }
        public int Executions { get; private set; }
        public string? ExecutedProject { get; private set; }
        public ReleaseSelection? ExecutedSelection { get; private set; }

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
                var selected = context.Selection!;
                var hint = context.ProjectId == "a" && PreviousVersion is { } version
                    ? new PreviousInstallation(selected with { Version = version }, "2026-11-26T00:00:00Z")
                    : null;
                var installed = context.ProjectId == "b" || RecommendationInstalled;
                var runtime = context.ProjectId == "b" ? new(RuntimeState.Quiescent, "fixture", "fixture") : Runtime;
                var status = new ManagedProjectStatus(installed, installed ? selected.Version : null, selected.Version,
                    !installed, 0, 0, false, hint)
                {
                    RecommendationHistory = LastActiveVersion is { } active
                        ? new(selected with { Version = active })
                        : null
                };
                return new(config, selected, status, runtime);
            }
            finally { _concurrentChecks--; }
        }

        public Task<RuntimeObservation?> ReadAuthorizedRuntimeAsync(LauncherUiOperationContext context, LauncherConfig config,
            CancellationToken token = default)
        {
            Assert.Equal("exact", context.VersionPolicy);
            Assert.Equal(LastActiveVersion ?? PreviousVersion, context.RequestedVersion);
            Assert.Equal(LastActiveVersion ?? PreviousVersion, context.Selection?.Version);
            RuntimeReads.Add(context.Selection!);
            return Task.FromResult(PreviousRuntime);
        }

        public async Task<LauncherUiOperationResult> ExecuteAsync(LauncherUiOperationContext context, LauncherConfig config,
            bool repair, bool launch, Action<LauncherProgress> progress, FileLogger? logger, CancellationToken token = default)
        {
            Executions++;
            ExecutedProject = context.ProjectId;
            ExecutedSelection = context.Selection;
            ResumedOperation = config.UiResumeOperation;
            Assert.Equal(ResumedOperation is null, launch);
            Assert.False(repair);
            try
            {
                if (CompleteExecution is { } barrier) await barrier.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (RetainCancelledDownload)
            {
                throw new LauncherUiCancelledException(new(2, Guid.NewGuid().ToString("N"), "owner", "session",
                    context.Selection, "Cancelled", true, DateTimeOffset.UtcNow.ToString("O"), new string('a', 64)), true);
            }
            VersionedReleasePaths.Bind(config, context.Selection!);
            return new(config, context.Selection, new(true, "1.0.0", "1.0.0", false, 0, 0, false),
                new(RuntimeState.Running, "fixture", "fixture"), LauncherUiCompletion.Completed);
        }
    }
}
