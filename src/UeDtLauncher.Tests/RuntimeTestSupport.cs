namespace UeDtLauncher.Tests;

internal static class RuntimeTestSupport
{
    // Fixtures containing fake files have no payload processes. Declare that fact explicitly;
    // production must never infer it merely from missing legacy PID metadata.
    internal static void Stopped(LauncherConfig config) => RuntimeStore.Write(config, new()
    { InstallationId=RuntimeStore.InstallationId(config), State=RuntimeState.Quiescent, Origin="operator-confirmed", Requester=RuntimeIdentities.Current() });
    internal static RuntimeRecord Active(LauncherConfig config, RuntimeState state, RuntimeIdentity? host = null) => new()
    {
        InstallationId=RuntimeStore.InstallationId(config), State=state, Origin="supervised", Requester=RuntimeIdentities.Current(),
        Host=host ?? RuntimeIdentities.Current(), AttemptId=Guid.NewGuid().ToString("N"), TokenHash=new string('a',64),
        ManifestHash=new string('b',64), EntryPoint=Path.Combine(config.InstallDir,"game.exe")
    };
}
