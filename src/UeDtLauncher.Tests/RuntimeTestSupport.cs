namespace UeDtLauncher.Tests;

internal static class RuntimeTestSupport
{
    // Fixtures containing fake files have no payload processes. Declare that fact explicitly;
    // production must never infer it merely from missing legacy PID metadata.
    internal static void Stopped(LauncherConfig config) => RuntimeStore.Write(config, new()
    { InstallationId=RuntimeStore.InstallationId(config), State=RuntimeState.Quiescent, Origin="test-fixture-no-processes" });
}
