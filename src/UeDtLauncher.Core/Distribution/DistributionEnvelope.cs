namespace UeDtLauncher;

public sealed record DistributionEnvelope(string Payload, string SignatureDocument);

public sealed record ReleaseSelection(string ProjectId, string Environment, string Channel, string Platform, string Version)
{
    public string ReleaseId => string.Join("/", ProjectId, Environment, Channel, Version, Platform);
    public void Validate()
    {
        ReleaseSidecar.Segment(ProjectId); ReleaseSidecar.Segment(Version);
        KnownValues.ValidateReleaseTuple(Platform, Environment, Channel);
    }
}
