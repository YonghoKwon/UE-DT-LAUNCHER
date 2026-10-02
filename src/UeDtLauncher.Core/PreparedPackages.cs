namespace UeDtLauncher;

internal sealed record PreparedPackageFile(
    string PackageId,
    string SourcePath,
    string RelativeInstallPath);

internal sealed class PreparedPackages
{
    internal static PreparedPackages Empty { get; } = new();

    internal List<PreparedPackageFile> Files { get; } = new();
    internal Dictionary<string, string> AppliedPackageHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<string> SkippedOptionalPackages { get; } = new();
}
