using System.Text;

namespace UeDtLauncher.Gui;

internal static class LauncherReleaseNotes
{
    internal const string EmptyMessage = "변경 내역이 등록되지 않았습니다";

    internal readonly record struct Section(string Text, bool IsHeading);

    internal static IEnumerable<Section> Format(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            yield return new(EmptyMessage, false);
            yield break;
        }

        var plain = new StringBuilder();
        var lines = notes.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = index == lines.Length - 1 ? "" : "\n";
            var markerEnd = line.IndexOf(' ');
            if (markerEnd is >= 1 and <= 6 && !string.IsNullOrWhiteSpace(line[(markerEnd + 1)..]))
            {
                var marker = line[..markerEnd];
                if (marker.All(c => c == '#'))
                {
                    if (plain.Length > 0)
                    {
                        yield return new(plain.ToString(), false);
                        plain.Clear();
                    }
                    yield return new(line[(markerEnd + 1)..] + separator, true);
                    continue;
                }
                if (marker is "-" or "*")
                {
                    plain.Append("• ").Append(line.AsSpan(markerEnd + 1)).Append(separator);
                    continue;
                }
            }
            plain.Append(line).Append(separator);
        }
        if (plain.Length > 0) yield return new(plain.ToString(), false);
    }
}
