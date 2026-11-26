using System.Text;

namespace UeDtLauncher.Gui;

internal static class LauncherReleaseNotes
{
    internal const string EmptyMessage = "변경 내역이 등록되지 않았습니다";
    internal const int MaxNoteBytes = 64 * 1024;
    internal const int MaxFormattedLines = 128;

    internal readonly record struct Section(string Text, bool IsHeading);

    internal static IEnumerable<Section> Format(string? notes)
    {
        var safe = Sanitize(notes ?? "");
        if (string.IsNullOrWhiteSpace(safe))
        {
            yield return new(EmptyMessage, false);
            yield break;
        }

        var plain = new StringBuilder();
        var remainingStyles = MaxFormattedLines;
        var remainingExpansionBytes = MaxNoteBytes - Encoding.UTF8.GetByteCount(safe);
        var lines = safe.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = index == lines.Length - 1 ? "" : "\n";
            var markerEnd = line.IndexOf(' ');
            if (remainingStyles > 0 && markerEnd is >= 1 and <= 6 && !string.IsNullOrWhiteSpace(line[(markerEnd + 1)..]))
            {
                var marker = line[..markerEnd];
                if (marker.All(c => c == '#'))
                {
                    remainingStyles--;
                    if (plain.Length > 0)
                    {
                        yield return new(plain.ToString(), false);
                        plain.Clear();
                    }
                    yield return new(line[(markerEnd + 1)..] + separator, true);
                    continue;
                }
                if (marker is "-" or "*" && remainingExpansionBytes >= 2)
                {
                    remainingStyles--;
                    remainingExpansionBytes -= 2; // A UTF-8 bullet takes two more bytes than the ASCII marker.
                    plain.Append("• ").Append(line.AsSpan(markerEnd + 1)).Append(separator);
                    continue;
                }
            }
            plain.Append(line).Append(separator);
        }
        if (plain.Length > 0) yield return new(plain.ToString(), false);
    }

    private static string Sanitize(string notes)
    {
        var safe = new StringBuilder();
        Span<char> encoded = stackalloc char[2];
        var bytes = 0;
        foreach (var rune in notes.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > MaxNoteBytes) break;
            if (rune.Value is 0x061C or 0x200E or 0x200F or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x206F)
                continue;
            safe.Append(encoded[..rune.EncodeToUtf16(encoded)]);
        }
        return safe.ToString().ReplaceLineEndings("\n");
    }
}
