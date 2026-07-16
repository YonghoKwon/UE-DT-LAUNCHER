namespace UeDtLauncher;

/// <summary>
/// Canonical value sets the launcher understands. The client GUI dropdowns, general-profile
/// validation, and catalog matching all use these exact strings, so the release-publishing CLI
/// validates against them to reject typos (e.g. "windows-64") before they reach a catalog.
/// </summary>
public static class KnownValues
{
    public static readonly IReadOnlyList<string> Platforms = new[] { "windows-x64", "linux-x64" };
    public static readonly IReadOnlyList<string> Environments = new[] { "prod", "dev" };
    public static readonly IReadOnlyList<string> Channels = new[] { "stable", "beta", "dev" };

    public static void ValidatePlatform(string value) => Ensure("--platform", value, Platforms);
    public static void ValidateEnvironment(string value) => Ensure("--environment", value, Environments);
    public static void ValidateChannel(string value) => Ensure("--channel", value, Channels);

    public static void ValidateReleaseTuple(string platform, string environment, string channel)
    {
        ValidatePlatform(platform);
        ValidateEnvironment(environment);
        ValidateChannel(channel);
    }

    private static void Ensure(string argName, string value, IReadOnlyList<string> allowed)
    {
        if (allowed.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase))) return;
        throw new ArgumentException($"Invalid {argName} '{value}'. Allowed: {string.Join(", ", allowed)}.");
    }
}
