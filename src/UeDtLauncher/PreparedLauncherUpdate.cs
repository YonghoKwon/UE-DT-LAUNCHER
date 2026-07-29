namespace UeDtLauncher;

internal sealed class PreparedLauncherUpdate : IDisposable
{
    private readonly SingleInstanceLock _instanceLock;
    private bool _disposed;

    internal PreparedLauncherUpdate(
        LauncherManifest remoteManifest,
        string manifestJson,
        LauncherManifest? localManifest,
        InstallState? localState,
        UpdatePlan plan,
        PreparedPackages packages,
        IReadOnlyDictionary<string, string> packageHashes,
        bool packagePreparationRequested,
        SingleInstanceLock instanceLock)
    {
        RemoteManifest = remoteManifest;
        ManifestJson = manifestJson;
        LocalManifest = localManifest;
        LocalState = localState;
        Plan = plan;
        Packages = packages;
        PackageHashes = packageHashes;
        PackagePreparationRequested = packagePreparationRequested;
        _instanceLock = instanceLock;
    }

    internal LauncherManifest RemoteManifest { get; }
    internal string ManifestJson { get; }
    internal LauncherManifest? LocalManifest { get; }
    internal InstallState? LocalState { get; }
    internal UpdatePlan Plan { get; }
    internal PreparedPackages Packages { get; }
    internal IReadOnlyDictionary<string, string> PackageHashes { get; }
    internal bool PackagePreparationRequested { get; }
    internal bool HasLiveChanges => Plan.HasChanges || Packages.Files.Count > 0;

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _instanceLock.Dispose();
        _disposed = true;
    }
}
