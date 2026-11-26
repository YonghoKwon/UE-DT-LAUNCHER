using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace UeDtLauncher.Gui;

public sealed partial class MainWindow
{
    private Control GeneralReleaseHelp()
    {
        var release = SelectedCatalogRelease();
        var body = SettingsPanel();
        if (release is not null)
            body.Children.Add(Txt($"{_selectedProject.DisplayName} {release.Version}", LauncherVisualTokens.FontSection, true));
        body.Children.Add(ReleaseNotesView(release?.Notes, "current-release-note", "현재 버전 변경 내역"));
        if (release is not null)
        {
            var current = CatalogSnapshotService.VersionSortKey.Parse(release.Version);
            var previousVersions = MatchingReleases()
                .Select(r => (Release: r, Version: CatalogSnapshotService.VersionSortKey.Parse(r.Version)))
                .Where(r => r.Version.CompareTo(current) < 0)
                .OrderByDescending(r => r.Version)
                .Select(r => r.Release)
                .ToArray();
            if (previousVersions.Length > 0)
            {
                var previous = SettingsPanel();
                foreach (var older in previousVersions)
                {
                    var note = Identify(ScaleHeader(new Expander { Header = older.Version }),
                        "previous-release-" + older.Version, $"{older.Version} 변경 내역");
                    note.PropertyChanged += (_, args) =>
                    {
                        if (args.Property == Expander.IsExpandedProperty && note.IsExpanded)
                            note.Content ??= ReleaseNotesView(older.Notes, "previous-release-note-" + older.Version, $"{older.Version} 변경 내역");
                    };
                    previous.Children.Add(note);
                }
                body.Children.Add(Identify(ScaleHeader(new Expander { Header = "이전 버전", Content = previous }),
                    "previous-release-notes", "이전 버전 변경 내역"));
            }
        }
        body.Children.Add(Muted("프로그램이 실행 중이면 정상 종료 후 다시 확인해 주세요. 창을 닫아도 실행 중인 프로그램은 종료되지 않습니다.", LauncherVisualTokens.FontBody));
        return body;
    }

    private ScrollViewer ReleaseNotesView(string? notes, string id, string name)
    {
        var text = Txt("", LauncherVisualTokens.FontBody, false);
        foreach (var section in LauncherReleaseNotes.Format(notes))
        {
            var run = new Run(section.Text);
            if (section.IsHeading)
            {
                run.FontWeight = FontWeight.SemiBold;
                run.FontSize = LauncherVisualTokens.FontSection * _preferences.TextScale;
            }
            text.Inlines!.Add(run);
        }
        return Identify(new ScrollViewer
        {
            Content = text,
            Focusable = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 300
        }, id, name);
    }
}
