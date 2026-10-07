namespace UeDtLauncher;

public sealed class NoPromotedReleaseException() : InvalidOperationException("관리자가 실행 버전을 지정하지 않았습니다.");
public static class CatalogRecommendation
{
    public const string ExplicitPolicy = "explicit-promotion-v1";
    public static void Validate(string? policy)
    {
        if (policy is not null && policy != ExplicitPolicy) throw new InvalidDataException("Unsupported catalog selection policy.");
    }
    public static T? Find<T>(string? policy, IEnumerable<T> candidates, Func<T, bool> isLatest) where T : class
    {
        Validate(policy); var values = candidates.ToArray(); var latest = values.Where(isLatest).ToArray();
        if (policy == ExplicitPolicy && latest.Length > 1) throw new InvalidDataException("Catalog has conflicting promoted recommendations.");
        return latest.FirstOrDefault() ?? (policy == ExplicitPolicy ? null : values.FirstOrDefault());
    }
    internal static string RequestUrl(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DistributionServerUrl)) return config.CatalogUrl!;
        var uri = new UriBuilder(config.CatalogUrl!);
        if (uri.Query.Length > 0 && uri.Query != "?selectionPolicy="+ExplicitPolicy)
            throw new InvalidDataException("Distribution catalog query is unsupported.");
        uri.Query = "selectionPolicy="+ExplicitPolicy; return uri.Uri.AbsoluteUri;
    }
}
