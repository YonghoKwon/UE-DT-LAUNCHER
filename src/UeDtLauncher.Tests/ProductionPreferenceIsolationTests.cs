using UeDtLauncher.Gui;
using Xunit;

namespace UeDtLauncher.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class PreferenceEnvironmentCollection
{
    public const string Name = "UI preferences environment";
}

[Collection(PreferenceEnvironmentCollection.Name)]
public class ProductionPreferenceIsolationTests
{
    [Fact]
    public async Task ProcessOverrideIsolatesPreferenceReadsAndWrites()
    {
        var original = Environment.GetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride);
        var root = Path.Combine(Path.GetTempPath(), "uedt-ui-isolation-" + Guid.NewGuid().ToString("N"));
        var userFile = Path.Combine(root, "user", "ui-preferences.json");
        var captureDirectory = Path.Combine(root, "capture");
        try
        {
            var userPreferences = new LauncherUiPreferences(2, true);
            await userPreferences.SaveAsync(userFile);
            var userBytes = await File.ReadAllBytesAsync(userFile);

            Environment.SetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride, captureDirectory);
            Assert.Equal(Path.Combine(captureDirectory, "ui-preferences.json"), LauncherUiPreferences.DefaultPath);
            Assert.Equal(new LauncherUiPreferences(), LauncherUiPreferences.Load());
            var capturePreferences = new LauncherUiPreferences(1.25, false);
            await capturePreferences.SaveAsync();
            Assert.Equal(capturePreferences, LauncherUiPreferences.Load());

            // An independent user save during the capture remains authoritative for the user.
            await new LauncherUiPreferences(1.5, true).SaveAsync(userFile);
            var updatedUserBytes = await File.ReadAllBytesAsync(userFile);
            Assert.False(userBytes.SequenceEqual(updatedUserBytes));
            await new LauncherUiPreferences(1, true).SaveAsync();
            Assert.Equal(updatedUserBytes, await File.ReadAllBytesAsync(userFile));
            Assert.Equal(new LauncherUiPreferences(1, true), LauncherUiPreferences.Load());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride, original);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RelativeOverrideCannotFallBackToUserStorage()
    {
        var original = Environment.GetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride);
        try
        {
            Environment.SetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride, "capture-prefs");
            Assert.Equal(new LauncherUiPreferences(), LauncherUiPreferences.Load());
            await Assert.ThrowsAsync<IOException>(() => new LauncherUiPreferences(1, true).SaveAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LauncherUiPreferences.DirectoryOverride, original);
        }
    }
}
