using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private bool _developerNotesExpanded;

    private Control EnterpriseFilters()
    {
        var track = HasProject ? MatchingReleases().ToArray() : [];
        var release = HasProject ? SelectedCatalogRelease() : null;
        var environment = DeveloperFilter("환경", _config.Environment, ["prod", "dev"], value => _config.Environment = value);
        var channel = DeveloperFilter("채널", _config.Channel, ["stable", "beta", "dev"], value => _config.Channel = value);
        var policy = DeveloperFilter("버전 정책", _config.VersionPolicy, ["latest", "exact"], value => _config.VersionPolicy = value);
        var exact = _config.VersionPolicy == "exact";
        var versionLabel = exact ? "요청 버전" : "선택 버전";
        Control version = exact && track.Length != 0
            ? DeveloperFilter(versionLabel, release?.Version, track.Select(item => item.Version).ToArray(),
                value => _config.RequestedVersion = value)
            : new StackPanel
            {
                Spacing = LauncherVisualTokens.SpaceXs,
                Children = { Muted(versionLabel, LauncherVisualTokens.FontCaption), Txt(release?.Version ?? "선택 없음", LauncherVisualTokens.FontBody, true) }
            };

        var columns = CurrentLayoutWidth() >= 1100 && _preferences.TextScale <= 1.5 ? 4 : 2;
        var filters = new Grid
        {
            ColumnDefinitions = new(columns == 4 ? "*,*,*,2*" : "*,*"),
            RowDefinitions = new(columns == 4 ? "Auto" : "Auto,Auto"),
            ColumnSpacing = LauncherVisualTokens.SpaceMd,
            RowSpacing = LauncherVisualTokens.SpaceSm
        };
        if (release is not null)
        {
            var withBadge = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = LauncherVisualTokens.SpaceSm };
            withBadge.Children.Add(version);
            var badge = DeveloperRecommendationBadge(release);
            badge.VerticalAlignment = VerticalAlignment.Bottom;
            withBadge.Children.Add(At(badge, 1));
            version = withBadge;
        }
        var fields = new[] { environment, channel, policy, version };
        for (var index = 0; index < fields.Length; index++)
        {
            Grid.SetRow(fields[index], index / columns);
            filters.Children.Add(At(fields[index], index % columns));
        }
        return Identify(Card(filters, LauncherVisualTokens.SpaceMd), "developer-release-filters", "배포 선택");
    }

    private Control DeveloperFilter(string name, string? current, IReadOnlyList<string> values, Action<string> apply)
    {
        var label = Muted(name, LauncherVisualTokens.FontCaption);
        var combo = Identify(new ComboBox
        {
            ItemsSource = values,
            SelectedItem = values.FirstOrDefault(value => string.Equals(value, current, StringComparison.OrdinalIgnoreCase)),
            PlaceholderText = "선택",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = LauncherVisualTokens.FontBody * _preferences.TextScale,
            MinHeight = 36
        }, "filter-" + name, name);
        AutomationProperties.SetLabeledBy(combo, label);
        _selectionControls.Add(combo);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || _running || combo.SelectedItem is not string value ||
                string.Equals(value, current, StringComparison.OrdinalIgnoreCase)) return;
            apply(value);
            SelectionChanged();
            Build();
        };
        return new StackPanel { Spacing = LauncherVisualTokens.SpaceXs, Children = { label, combo } };
    }

    private Border DeveloperRecommendationBadge(CatalogReleaseOption release)
    {
        var label = Txt(DeveloperRecommendationLabel(release), LauncherVisualTokens.FontCaption, true);
        label.Foreground = release.IsLatest ? StatusBrush(LauncherStatusTone.Normal) : MutedBrush();
        return new Border
        {
            BorderBrush = LauncherVisualTokens.Brush(LauncherVisualTokens.BorderColor(IsDeveloper, HighContrast)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(LauncherVisualTokens.RadiusControl),
            Padding = new Thickness(LauncherVisualTokens.SpaceSm, LauncherVisualTokens.SpaceXs),
            Child = label
        };
    }

    private static string DeveloperRecommendationLabel(CatalogReleaseOption release) =>
        release.IsLatest ? "현재 추천" : "추천 아님";

    private Control DeveloperSelectionPanels()
    {
        var release = SelectedCatalogRelease();
        var recommendation = MatchingReleases().FirstOrDefault(item => item.IsLatest);
        var deployment = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm };
        deployment.Children.Add(Txt("선택한 배포", LauncherVisualTokens.FontSection, true));
        if (release is null)
            deployment.Children.Add(Muted("선택한 배포 없음", LauncherVisualTokens.FontBody));
        else
        {
            deployment.Children.Add(new WrapPanel
            {
                ItemSpacing = LauncherVisualTokens.SpaceSm,
                Children = { Txt(release.Version, LauncherVisualTokens.FontStatus, true), DeveloperRecommendationBadge(release) }
            });
            var notes = Identify(ScaleHeader(new Expander
            {
                Header = "바뀐 점",
                IsExpanded = _developerNotesExpanded,
                Content = ReleaseNotesView(release.Notes, "developer-selected-notes", "선택한 배포의 변경 내역")
            }), "developer-release-notes", "바뀐 점");
            notes.PropertyChanged += (_, args) =>
            {
                if (args.Property == Expander.IsExpandedProperty) _developerNotesExpanded = notes.IsExpanded;
            };
            deployment.Children.Add(KeyValue("현재 추천 버전", recommendation?.Version ?? "지정된 버전 없음"));
            deployment.Children.Add(notes);
        }

        var status = !UsesDistributionServer || release is not null ? _viewModel.ProjectStatus : null;
        var installation = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm };
        installation.Children.Add(Txt("이 선택의 설치 상태", LauncherVisualTokens.FontSection, true));
        installation.Children.Add(KeyValue("설치 여부", status switch
        {
            { IsInstalled: true } => "설치됨",
            { IsInstalled: false } => "미설치",
            _ => "확인 필요"
        }));
        if (status is not null)
        {
            if (status.IsInstalled && status.InstalledVersion is { } version)
                installation.Children.Add(KeyValue("설치 버전", version));
            installation.Children.Add(KeyValue("백업", status.HasBackup ? "있음" : "없음"));
            if (status.PreviousInstallation is { } previous)
                installation.Children.Add(KeyValue("마지막으로 쓴 버전", $"{previous.Release.Version} (기록)"));
        }
        installation.Children.Add(EnterpriseButton("설치 폴더 열기", "open-folder", (_, _) => OpenInstallFolder(),
            tracked: false, icon: LauncherIconKind.Folder));

        var sideBySide = CurrentLayoutWidth() >= 1100 && _preferences.TextScale <= 1.5;
        var panels = new Grid
        {
            ColumnDefinitions = new(sideBySide ? "*,*" : "*"),
            RowDefinitions = new(sideBySide ? "Auto" : "Auto,Auto"),
            ColumnSpacing = LauncherVisualTokens.SpaceMd,
            RowSpacing = LauncherVisualTokens.SpaceMd
        };
        panels.Children.Add(Identify(Card(deployment), "release-info", "선택한 배포"));
        var installPanel = Identify(Card(installation), "developer-installation", "이 선택의 설치 상태");
        panels.Children.Add(sideBySide ? At(installPanel, 1) : AtRow(installPanel, 1));
        return panels;
    }

    private Control DeveloperVersionList()
    {
        var allowed = MatchingReleases().ToArray();
        var selected = SelectedCatalogRelease();
        var body = new StackPanel { Spacing = LauncherVisualTokens.SpaceSm };
        body.Children.Add(Txt("버전 목록", LauncherVisualTokens.FontSection, true));
        if (allowed.Length == 0)
        {
            body.Children.Add(Muted("이 배포 트랙에 허용된 버전이 없습니다.", LauncherVisualTokens.FontBody));
        }
        else
        {
            var items = allowed.Select(DeveloperVersionItem).ToArray();
            var list = Identify(new ListBox
            {
                ItemsSource = items,
                SelectedIndex = Array.IndexOf(allowed, selected),
                MaxHeight = 230,
                Background = SurfaceBrush
            }, "developer-version-list", "허용된 버전");
            ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            _selectionControls.Add(list);
            list.SelectionChanged += (_, _) =>
            {
                if (_building || _running || list.SelectedIndex < 0) return;
                var release = allowed[list.SelectedIndex];
                if (_config.VersionPolicy == "exact" && ReferenceEquals(release, selected)) return;
                if (!MatchingReleases().Contains(release)) return;
                _config.VersionPolicy = "exact";
                _config.RequestedVersion = release.Version;
                _workspaceFocus = AutomationProperties.GetAutomationId(items[list.SelectedIndex]);
                SelectionChanged();
                Build();
            };
            if (list.SelectedItem is { } item)
                list.Loaded += (_, _) => list.ScrollIntoView(item);
            body.Children.Add(list);
        }
        return Card(body);
    }

    private ListBoxItem DeveloperVersionItem(CatalogReleaseOption release)
    {
        var firstLine = release.Notes?.Split(['\r', '\n'], 2)[0].Trim();
        var details = new StackPanel { Spacing = LauncherVisualTokens.SpaceXs };
        details.Children.Add(new WrapPanel
        {
            ItemSpacing = LauncherVisualTokens.SpaceSm,
            Children = { Txt(release.Version, LauncherVisualTokens.FontBody, true), DeveloperRecommendationBadge(release) }
        });
        if (!string.IsNullOrEmpty(firstLine))
        {
            var preview = Muted(firstLine, LauncherVisualTokens.FontCaption);
            preview.MaxLines = 1;
            preview.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(preview);
        }
        return Identify(new ListBoxItem
        {
            Content = details,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        }, "developer-version-" + release.Version, $"{release.Version}, {DeveloperRecommendationLabel(release)}");
    }

    private Control DeveloperBuildInformation() =>
        Identify(Card(new StackPanel
        {
            Spacing = LauncherVisualTokens.SpaceSm,
            Children =
            {
                Txt("정보", LauncherVisualTokens.FontSection, true),
                KeyValue("런처 빌드", LauncherDisplayVersion),
                KeyValue("운영체제", CurrentPlatform)
            }
        }), "developer-build-info", "런처 정보");
}
