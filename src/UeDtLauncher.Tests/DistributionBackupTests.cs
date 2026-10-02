using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;

public sealed class DistributionBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "backup-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DistributionSettings settings;
    private readonly IntakeStore store;
    public DistributionBackupTests()
    {
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(Path.Combine(root, "private.pem"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(root, "policy.json"), "{\"clients\":[]}");
        settings = new() { Root = Path.Combine(root, "server"), SigningKeyPath = Path.Combine(root, "private.pem"), PolicyPath = Path.Combine(root, "policy.json"), AuthenticationMode = "request-signature-v1" };
        store = new(settings); File.WriteAllText(Path.Combine(settings.Root, "incoming", "private-upload.txt"), "payload");
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    [Fact]
    public void MaintenanceExcludesLiveOperationsWithoutStoppingThem()
    {
        using var serve = DistributionMaintenanceLease.Acquire(settings.Root, false);
        using var second = DistributionMaintenanceLease.Acquire(settings.Root, false);
        Assert.Throws<IOException>(() => DistributionMaintenanceLease.Acquire(settings.Root, true));
    }
    [Fact]
    public async Task BackupIsVerifiableHasNoSigningPrivateKeyAndDetectsMutation()
    {
        var backup = Path.Combine(root, "backup"); var result = await DistributionBackup.CreateAsync(settings, backup);
        Assert.DoesNotContain(result.Files, file => file.Path.Contains("private.pem"));
        await DistributionBackup.VerifyAsync(backup);
        File.AppendAllText(Path.Combine(backup, "content/incoming/private-upload.txt"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => DistributionBackup.VerifyAsync(backup));
    }
    [Fact]
    public async Task StagedRootIsBlockedAndActivationUsesLatestRevocationsAndSequence()
    {
        var tokens = new DistributionTokens(store); var token = tokens.Issue("pc"); var id = tokens.List().Single().Id;
        var backup = Path.Combine(root, "backup"); await DistributionBackup.CreateAsync(settings, backup);
        var target = Path.Combine(root, "restored"); await DistributionBackup.StageAsync(backup, target, settings);
        var targetSettings = new DistributionSettings { Root = target, SigningKeyPath = settings.SigningKeyPath, PolicyPath = settings.PolicyPath, AuthenticationMode = settings.AuthenticationMode };
        Assert.Throws<InvalidDataException>(() => DistributionHttp.CreateApplication(new(targetSettings)));
        tokens.RevokeId(id);
        using (var db = store.Open()) { using var q = db.CreateCommand(); q.CommandText = "UPDATE sequence SET value=1000"; q.ExecuteNonQuery(); }
        await DistributionBackup.ActivateAsync(target, settings, true);
        var restored = new IntakeStore(targetSettings); Assert.Null(new DistributionTokens(restored).Authenticate(token));
        using var check = restored.Open(); using var sequence = check.CreateCommand(); sequence.CommandText = "SELECT value FROM sequence"; Assert.Equal(1000L, sequence.ExecuteScalar());
        Assert.False(File.Exists(Path.Combine(target, "restore-staged.json")));
    }
    [Fact]
    public async Task OriginMismatchAndMissingCurrentIntakeRemainStaged()
    {
        var backup = Path.Combine(root, "backup"); await DistributionBackup.CreateAsync(settings, backup);
        var wrong = new DistributionSettings { Root = settings.Root, PublicUrl = "https://different.example", SigningKeyPath = settings.SigningKeyPath };
        await Assert.ThrowsAsync<InvalidDataException>(() => DistributionBackup.StageAsync(backup, Path.Combine(root, "wrong"), wrong));
        var target = Path.Combine(root, "restore"); await DistributionBackup.StageAsync(backup, target, settings);
        store.Save(new("new-job", Path.Combine(settings.Root, "incoming/new"), "pending", Path.Combine(settings.Root, "archive/new"), null));
        await Assert.ThrowsAsync<InvalidDataException>(() => DistributionBackup.ActivateAsync(target, settings, true));
        Assert.True(File.Exists(Path.Combine(target, "restore-staged.json")));
    }
    [Fact]
    public async Task ReadOnlyPlanAndVerificationDoNotInitializeServer()
    {
        var missing = new DistributionSettings { Root = Path.Combine(root, "not-created") };
        _ = DistributionBackup.Plan(missing); Assert.False(Directory.Exists(missing.Root));
        await Assert.ThrowsAnyAsync<Exception>(() => DistributionBackup.VerifyAsync(missing.Root)); Assert.False(Directory.Exists(missing.Root));
    }
}
