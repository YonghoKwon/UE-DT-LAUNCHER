namespace UeDtLauncher;

public sealed class CliArgumentException(string safeMessage) : ArgumentException(safeMessage);

/// <summary>Validates supplied options before any command performs I/O. Unknown options retain legacy ignore behavior.</summary>
public static class CliArguments
{
    public static void Validate(string[] args, IEnumerable<string> valueOptions)
    {
        var values = new HashSet<string>(valueOptions, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal)) continue;
            if (!seen.Add(option)) throw new CliArgumentException("Duplicate option; supply each option only once.");
            if (!values.Contains(option)) continue;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliArgumentException("Value required after " + values.First(v => v.Equals(option, StringComparison.OrdinalIgnoreCase)) + ".");
            i++;
        }
    }

    public static string? Get(string[] args, string name)
    {
        Validate(args, [name]);
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : args[index + 1];
    }
}
