using System.Text.Json;
using Xunit;

namespace UeDtLauncher.Tests;

public class PerformanceProgressCompatibilityTests
{
    [Fact]
    public void LegacyThreeFieldManagedProgressRemainsCompatible()
    {
        const string legacy = "{\"stage\":\"Download\",\"message\":\"Downloading\",\"percent\":35}";
        var progress = JsonSerializer.Deserialize<ManagedAgentProgress>(legacy, JsonFiles.Options)!;
        Assert.Equal(new ManagedAgentProgress("Download", "Downloading", 35), progress);
        Assert.Null(progress.Performance);
        Assert.Null(progress.BytesDownloaded);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(progress, JsonFiles.Options));
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void AdditiveMetricsRoundTripThroughIpcAndGuiAdapterWithoutChangingMessage()
    {
        var metrics = new LauncherPerformanceMetrics(10, 20, 5, 0, NetworkBytes: 100, ReusedBytes: 900);
        var managed = new ManagedAgentProgress("DownloadProgress", "app.bin", 65, 1000, 1000, 2, 2, metrics);
        var json = JsonSerializer.Serialize(managed, JsonFiles.Options);
        var restored = JsonSerializer.Deserialize<ManagedAgentProgress>(json, JsonFiles.Options)!;
        Assert.Equal(managed, restored);
        var adapted = restored.ToLauncherProgress();
        Assert.Equal("app.bin", adapted.Message);
        Assert.Equal(1000, adapted.BytesDownloaded); // Logical completion, not network traffic.
        Assert.Equal(100, adapted.Performance!.NetworkBytes);
        Assert.Equal(900, adapted.Performance.ReusedBytes);
        Assert.Equal(metrics, JsonSerializer.Deserialize<LauncherProgress>(
            JsonSerializer.Serialize(adapted, JsonFiles.Options), JsonFiles.Options)!.Performance);
        // A v1 reader that knows only the original fields ignores every additive field.
        var oldReader = JsonSerializer.Deserialize<LegacyProgress>(json, JsonFiles.Options)!;
        Assert.Equal(new LegacyProgress("DownloadProgress", "app.bin", 65), oldReader);
    }

    private sealed record LegacyProgress(string Stage, string Message, double? Percent);
}
