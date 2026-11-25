using System.Globalization;

namespace UeDtLauncher.Gui;

public enum ProjectVisualKind
{
    Hero,
    Thumbnail
}

public sealed record ProjectVisualAsset(
    string? ResolvedPath,
    string Initials,
    int FallbackVariant,
    string? FailureReason = null)
{
    public bool HasImage => !string.IsNullOrWhiteSpace(ResolvedPath);
}

public static class ProjectVisualResolver
{
    public const long MaxAssetBytes = 20L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions =
        new([".png", ".jpg", ".jpeg", ".webp"], StringComparer.OrdinalIgnoreCase);

    public static ProjectVisualAsset Resolve(
        ProjectUiConfig project,
        string configPath,
        ProjectVisualKind kind)
    {
        var fallback = Fallback(project);
        var configured = kind == ProjectVisualKind.Hero ? project.HeroPath : project.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(configured))
            return fallback with { FailureReason = "not-configured" };

        try
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
            var resolved = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, configDirectory);
            if (!SupportedExtensions.Contains(Path.GetExtension(resolved)))
                return fallback with { FailureReason = "unsupported-format" };
            if (!File.Exists(resolved))
                return fallback with { FailureReason = "missing" };
            if (new FileInfo(resolved).Length > MaxAssetBytes)
                return fallback with { FailureReason = "too-large" };

            if (!HasSupportedSignature(resolved, Path.GetExtension(resolved)))
                return fallback with { FailureReason = "invalid-image" };
            return fallback with { ResolvedPath = resolved };
        }
        catch
        {
            return fallback with { FailureReason = "invalid-image" };
        }
    }

    internal static ProjectVisualAsset Fallback(ProjectUiConfig project) =>
        new(null, Initials(project.DisplayName, project.ProjectId), StableVariant(project.ProjectId));

    internal static string Initials(string? displayName, string? projectId)
    {
        var source = string.IsNullOrWhiteSpace(displayName) ? projectId : displayName;
        var parts = (source ?? "UE")
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = string.Concat(parts.Take(2).Select(part => StringInfo.GetNextTextElement(part).ToUpperInvariant()));
        return string.IsNullOrWhiteSpace(initials) ? "UE" : initials;
    }

    private static int StableVariant(string? projectId)
    {
        unchecked
        {
            var hash = 17;
            foreach (var value in projectId ?? "ue-dt") hash = hash * 31 + value;
            return (hash & int.MaxValue) % 3;
        }
    }

    private static bool HasSupportedSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return read >= 12
                   && header[..4].SequenceEqual("RIFF"u8)
                   && header[8..12].SequenceEqual("WEBP"u8);
        return false;
    }
}
