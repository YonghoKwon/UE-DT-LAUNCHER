namespace UeDtLauncher.Distribution;

public static class PromotionCatalog
{
    public const string SelectionPolicy = "explicit-promotion-v1";
    public static DistributionCatalog Build(PromotionSnapshot snapshot, IReadOnlyList<PublishedRelease> allowed, bool full,
        string origin, PublishedAssetCache assets, long sequence, DateTimeOffset now)
    {
        if (!snapshot.Ready) throw new InvalidDataException("Promotion migration required.");
        var ids = allowed.Select(r => r.ReleaseId).ToHashSet(StringComparer.Ordinal);
        var eligible = snapshot.Events.Where(e => ids.Contains(e.ReleaseId)).ToArray();
        var promoted = eligible.Select(e => e.ReleaseId).ToHashSet(StringComparer.Ordinal);
        var recommendations = eligible.GroupBy(e => e.Track).Select(g => g.Last().ReleaseId).ToHashSet(StringComparer.Ordinal);
        var visible = full ? allowed : allowed.Where(r => promoted.Contains(r.ReleaseId)).ToArray();
        var catalog = new DistributionCatalog { SchemaVersion = 2, SelectionPolicy = SelectionPolicy, Sequence = sequence, GeneratedAt = now.ToString("O"),
            IssuedAtUtc = now.ToString("O"), ExpiresAtUtc = now.AddMinutes(10).ToString("O") };
        foreach (var group in visible.GroupBy(r => r.Metadata.ProjectId))
        {
            var values = group.ToArray(); var projectEvents = eligible.Where(e => values.Any(r => r.ReleaseId == e.ReleaseId));
            var assetId = projectEvents.LastOrDefault()?.ReleaseId;
            var imageRelease = values.FirstOrDefault(r => r.ReleaseId == assetId);
            var images = imageRelease is null ? null : assets.Get(imageRelease);
            RemoteProjectAsset? Asset(PublishedAssetMetadata? value) => value is null || imageRelease is null ? null :
                new(origin.TrimEnd('/')+"/releases/"+imageRelease.ReleaseId+"/files/"+value.Path, value.Sha256, value.Size);
            catalog.Projects.Add(new DistributionProject { ProjectId = group.Key, DisplayName = values[0].Metadata.DisplayName,
                Hero = Asset(images?.Hero), Thumbnail = Asset(images?.Thumbnail), Releases = values.Select(r => new DistributionRelease
                {
                    Version = r.Metadata.Version, Environment = r.Metadata.Environment, Channel = r.Metadata.Channel, Platform = r.Metadata.Platform,
                    Notes = r.Metadata.Notes, ManifestUrl = origin.TrimEnd('/')+"/releases/"+r.ReleaseId+"/manifest.json",
                    ManifestSignatureUrl = origin.TrimEnd('/')+"/releases/"+r.ReleaseId+"/manifest.json.sig",
                    AllowedClientProfiles = ["general", "developer"], IsLatest = recommendations.Contains(r.ReleaseId)
                }).ToList() });
        }
        return catalog;
    }
}
