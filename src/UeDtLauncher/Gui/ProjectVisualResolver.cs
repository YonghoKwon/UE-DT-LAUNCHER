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
        var initials = Initials(project.DisplayName, project.ProjectId);
        var variant = StableVariant(project.ProjectId);
        var configured = kind == ProjectVisualKind.Hero ? project.HeroPath : project.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(configured))
            return new ProjectVisualAsset(null, initials, variant, "not-configured");

        try
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory;
            var resolved = Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, configDirectory);
            if (!SupportedExtensions.Contains(Path.GetExtension(resolved)))
                return new ProjectVisualAsset(null, initials, variant, "unsupported-format");
            if (!File.Exists(resolved))
                return new ProjectVisualAsset(null, initials, variant, "missing");
            if (new FileInfo(resolved).Length > MaxAssetBytes)
                return new ProjectVisualAsset(null, initials, variant, "too-large");

            if (!HasSupportedSignature(resolved, Path.GetExtension(resolved)))
                return new ProjectVisualAsset(null, initials, variant, "invalid-image");
            return new ProjectVisualAsset(resolved, initials, variant);
        }
        catch
        {
            return new ProjectVisualAsset(null, initials, variant, "invalid-image");
        }
    }

    internal static string Initials(string? displayName, string? projectId)
    {
        var source = string.IsNullOrWhiteSpace(displayName) ? projectId : displayName;
        var parts = (source ?? "UE")
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initials = string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
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
