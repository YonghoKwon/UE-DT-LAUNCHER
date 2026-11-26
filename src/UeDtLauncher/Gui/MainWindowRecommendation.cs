using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace UeDtLauncher.Gui;

internal sealed record RecommendationChange(ReleaseSelection Recommended, ReleaseSelection Previous)
{
    internal static RecommendationChange? Find(string policy, CatalogReleaseOption? selected, ManagedProjectStatus? status)
    {
        if (policy != "latest" || selected is not { IsLatest: true } release ||
            status?.PreviousInstallation?.Release is not { } previous ||
            !string.Equals(status.AvailableVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            return null;

        var recommended = new ReleaseSelection(release.ProjectId, release.Environment, release.Channel, release.Platform, release.Version);
        if (!string.Equals(previous.ProjectId, recommended.ProjectId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.Environment, recommended.Environment, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.Channel, recommended.Channel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(previous.Platform, recommended.Platform, StringComparison.OrdinalIgnoreCase) ||
            !TryVersion(recommended.Version, out var current) ||
            !TryVersion(previous.Version, out var installed) || current >= installed)
            return null;

        return new(recommended, previous);
    }

    private static bool TryVersion(string? label, out Version version)
    {
        if (Version.TryParse(label?.TrimStart('v', 'V'), out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }
        version = new Version();
        return false;
    }
}

public sealed partial class MainWindow
{
    private const string RecommendationBadgeText = "권장 버전 변경";
    private readonly HashSet<ReleaseSelection> _shownRecommendations = [];
    private ReleaseSelection? _explainedRecommendation;
    private RecommendationChange? _checkedRecommendation;

    private RecommendationChange? LowerRecommendation() =>
        RecommendationChange.Find(_config.VersionPolicy, SelectedCatalogRelease(), _viewModel.ProjectStatus);

    private bool RecommendationExplained(RecommendationChange change) =>
        _shownRecommendations.Contains(change.Recommended) || _explainedRecommendation == change.Recommended;

    private Border RecommendationBadge(string id)
    {
        var text = Identify(Txt(RecommendationBadgeText, LauncherVisualTokens.FontCaption, true), id, RecommendationBadgeText);
        text.Foreground = StatusBrush(LauncherStatusTone.Caution);
        return new Border
        {
            Child = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(LauncherVisualTokens.SpaceSm, LauncherVisualTokens.SpaceXs),
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
            BorderBrush = text.Foreground,
            BorderThickness = new Thickness(1)
        };
    }

    private async Task<RuntimeObservation?> ObservePreviousRecommendationRuntimeAsync(ProjectUiConfig project,
        LauncherUiOperationContext context, RecommendationChange change,
        CancellationToken token = default)
    {
        // A separate authorized read checks the previous installation. The operation context stays on the recommendation.
        var previous = change.Previous;
        var read = context with { VersionPolicy = "exact", RequestedVersion = previous.Version, Selection = previous };
        try
        {
            var config = await RunProjectConfig(project, read);
            var result = await _uiBackend.CheckAsync(read, config, _ => { }, token);
            read.Validate(result);
            return result.Runtime;
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            return error.GetBaseException() is RuntimeBlockedException blocked ? blocked.Observation : null;
        }
    }

    private void OfferRecommendationChange()
    {
        if (IsDeveloper || _running || _cardOperation || ShowsProjectCards || !IsVisible ||
            _openDialogs.Count != 0 || !ShowRecommendationBadge || _checkedRecommendation is not { } change || LowerRecommendation() != change ||
            !_shownRecommendations.Add(change.Recommended))
            return;

        var context = DisplaySelectionContext();
        var state = _viewModel.GeneralState;
        var canChange = !_readOnlyRecoveryFollowUp && !SelectedInstallationDamaged &&
            state is GeneralLauncherState.NotInstalled or GeneralLauncherState.UpdateAvailable or GeneralLauncherState.Ready;
        var guidance = state == GeneralLauncherState.RuntimeBlocked
            ? _installState == "실행 중"
                ? "프로그램이 실행 중입니다. 종료한 뒤 다시 확인해 주세요."
                : "실행 상태를 확인한 뒤 권장 버전으로 변경할 수 있습니다."
            : SelectedInstallationDamaged
                ? "설치 파일 확인이 필요합니다. 문제 해결을 먼저 실행해 주세요."
                : _viewModel.ProjectStatus?.IsInstalled == true
                    ? "권장 버전이 설치되어 바로 실행할 수 있습니다."
                    : "권장 버전을 실행하려면 다운로드와 설치가 필요합니다.";

        var body = new StackPanel
        {
            Spacing = LauncherVisualTokens.SpaceLg,
            Children =
            {
                Txt($"{_selectedProject.DisplayName}의 권장 버전이 {change.Recommended.Version}으로 바뀌었습니다.", LauncherVisualTokens.FontBody, true),
                Txt($"이 PC에서 마지막으로 쓴 버전은 {change.Previous.Version}입니다(표시용 기록).", LauncherVisualTokens.FontBody, false),
                Txt(guidance, LauncherVisualTokens.FontBody, false),
                Muted("사용자 데이터는 버전별로 따로 보관되며 자동으로 옮겨지지 않습니다.", LauncherVisualTokens.FontBody)
            }
        };
        Window? dialog = null;
        var later = Identify(SecondaryButton("나중에", (_, _) => dialog!.Close(), 40), "recommendation-later", "나중에");
        var label = $"{change.Recommended.Version}로 변경";
        var accept = Identify(PrimaryButton(label, async (_, _) =>
        {
            if (!canChange || _running || context != DisplaySelectionContext() || LowerRecommendation() != change ||
                _viewModel.GeneralState != state)
                return;
            dialog!.Close();
            await RunAsync(false, true, change.Recommended);
        }, 40), "recommendation-change", label);
        accept.IsEnabled = canChange;
        Build();
        RecordSelectedCardStatus();
        dialog = AccessibleDialog("권장 버전이 바뀌었습니다", DialogCard(body), later, accept);
        _ = dialog.ShowDialog(this);
    }

    private bool ShowRecommendationBadge => !IsDeveloper &&
        _viewModel.GeneralState is GeneralLauncherState.NotInstalled or GeneralLauncherState.UpdateAvailable
            or GeneralLauncherState.Ready or GeneralLauncherState.RuntimeBlocked &&
        LowerRecommendation() is not null;
}
