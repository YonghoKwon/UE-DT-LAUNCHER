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
    [Fact]
    public async Task CleanupThenBackupRestoreRecognizesIntentionalMissingSnapshot()
    {
        var source=Path.Combine(settings.Root,"incoming/failed");var snapshot=Path.Combine(settings.Root,"processing/snapshot");Directory.CreateDirectory(source);Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(source,"zip"),"zip");File.WriteAllText(Path.Combine(snapshot,"zip"),"zip");store.Save(new("failed",source,"failed",snapshot,null));
        var plan=await RetentionMaintenance.PlanAsync(settings,["failed"],[]);await RetentionMaintenance.ApplyAsync(settings,plan,true);
        var backup=Path.Combine(root,"after-cleanup");await DistributionBackup.CreateAsync(settings,backup);
        var target=Path.Combine(root,"restored-after-cleanup");await DistributionBackup.StageAsync(backup,target,settings);await DistributionBackup.ActivateAsync(target,settings,true);
        Assert.False(Directory.Exists(Path.Combine(target,"processing/snapshot")));Assert.False(File.Exists(Path.Combine(target,"restore-staged.json")));
    }
    [Fact]
    public async Task ExtraReleasedFileAndNewWaitingSourceKeepRestoreBlocked()
    {
        var upload=Path.Combine(settings.Root,"incoming/waiting");Directory.CreateDirectory(upload);File.WriteAllText(Path.Combine(upload,"zip"),"zip");store.Save(new("waiting",upload,"waiting",null,null));
        var backup=Path.Combine(root,"waiting-backup");await DistributionBackup.CreateAsync(settings,backup);
        var target=Path.Combine(root,"waiting-restore");await DistributionBackup.StageAsync(backup,target,settings);
        File.WriteAllText(Path.Combine(target,"incoming/waiting/unlisted"),"extra");
        await Assert.ThrowsAsync<InvalidDataException>(()=>DistributionBackup.ActivateAsync(target,settings,true));Assert.True(File.Exists(Path.Combine(target,"restore-staged.json")));
    }
    [Fact]
    public async Task IncompleteRetentionBlocksBackupAndStagedRootRejectsAllStoreEntryPoints()
    {
        File.WriteAllText(Path.Combine(settings.Root,"retention-interrupted.json"),"{\"phase\":\"Deleting\"}");
        await Assert.ThrowsAsync<InvalidDataException>(()=>DistributionBackup.CreateAsync(settings,Path.Combine(root,"must-not-create")));
        Assert.False(Directory.Exists(Path.Combine(root,"must-not-create")));File.Delete(Path.Combine(settings.Root,"retention-interrupted.json"));
        var backup=Path.Combine(root,"staged-backup");await DistributionBackup.CreateAsync(settings,backup);var target=Path.Combine(root,"staged-target");await DistributionBackup.StageAsync(backup,target,settings);
        Assert.Throws<InvalidDataException>(()=>new IntakeStore(new(){Root=target}));
    }
}
