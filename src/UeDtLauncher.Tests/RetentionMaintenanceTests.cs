using Microsoft.Data.Sqlite;
using UeDtLauncher.Distribution;
using Xunit;

namespace UeDtLauncher.Tests;
public sealed class RetentionMaintenanceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"retention-"+Guid.NewGuid().ToString("N"));
    private readonly IntakeStore store;
    public RetentionMaintenanceTests()=>store=new(new(){Root=root});
    public void Dispose(){RetentionMaintenance.Boundary.Value=null;SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    private string Failed()
    {
        var source=Path.Combine(root,"incoming/failed");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"payload"),"failed");
        store.Save(new("failed",source,"failed",null,null));return source;
    }
    [Fact]
    public async Task ExplicitPlanAndConfirmationDeleteOnlyFailedAndRetainAudit()
    {
        var path=Failed();var plan=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);
        Assert.Single(plan.Candidates);Assert.Equal(6,plan.Candidates.Single().Bytes);
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,false));Assert.True(Directory.Exists(path));
        await RetentionMaintenance.ApplyAsync(store.Settings,plan,true);Assert.False(Directory.Exists(path));Assert.Equal("failed",store.Get("failed").State);
        await RetentionMaintenance.ApplyAsync(store.Settings,plan,true); // idempotent journal completion
        Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"payload"),"failed");
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));
        var fresh=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);Assert.NotEqual(plan.Fingerprint,fresh.Fingerprint);
        await RetentionMaintenance.ApplyAsync(store.Settings,fresh,true);Assert.False(Directory.Exists(path));
    }
    [Fact]
    public async Task PublishedPendingAndActiveReferencesAreProtected()
    {
        var path=Failed();store.Save(new("failed",path,"pending",null,null));
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]));
        var temp=Path.Combine(root,"processing/active");Directory.CreateDirectory(temp);
        using(var db=store.Open()){using var q=db.CreateCommand();q.CommandText="INSERT INTO active_work VALUES('active','owner',$path,NULL,'now')";q.Parameters.AddWithValue("$path",temp);q.ExecuteNonQuery();}
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.PlanAsync(store.Settings,[],["processing/active"]));
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.PlanAsync(store.Settings,[],["releases/demo"]));
    }
    [Fact]
    public async Task FileOrReferenceChangesRejectStalePlan()
    {
        var path=Failed();var plan=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);
        File.AppendAllText(Path.Combine(path,"payload"),"changed");
        await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));Assert.True(Directory.Exists(path));
    }
    [Theory][InlineData("quarantine")][InlineData("delete")]
    public async Task InterruptedCleanupResumesOnlyItsConfirmedJournal(string phase)
    {
        var path=Failed();var plan=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);
        RetentionMaintenance.Boundary.Value=stage=>{if(stage==phase)throw new IOException("injected");};
        await Assert.ThrowsAsync<IOException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));
        RetentionMaintenance.Boundary.Value=null;
        await RetentionMaintenance.ApplyAsync(store.Settings,plan,true);Assert.False(Directory.Exists(path));
    }
    [Theory][InlineData("delete")][InlineData("file-delete")]
    public async Task InterruptedDeletionNeverRecollectsRecreatedSource(string boundary)
    {
        var path=Failed();File.WriteAllText(Path.Combine(path,"second"),"second");var plan=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);
        var once=false;RetentionMaintenance.Boundary.Value=stage=>{if(stage==boundary && !once){once=true;throw new IOException("injected");}};
        await Assert.ThrowsAsync<IOException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));
        Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"payload"),"failed");
        RetentionMaintenance.Boundary.Value=null;await RetentionMaintenance.ApplyAsync(store.Settings,plan,true);
        Assert.Equal("failed",File.ReadAllText(Path.Combine(path,"payload")));
        using var db=store.Open();using var q=db.CreateCommand();q.CommandText="SELECT COUNT(*) FROM maintenance_deletions";Assert.Equal(1L,q.ExecuteScalar());
    }
    [Fact]
    public async Task RecreatedSourceBeforeMovementIsRejectedEvenWithSameBytes()
    {
        var path=Failed();var plan=await RetentionMaintenance.PlanAsync(store.Settings,["failed"],[]);
        RetentionMaintenance.Boundary.Value=stage=>{if(stage=="move-intent")throw new IOException("injected");};
        await Assert.ThrowsAsync<IOException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));
        Directory.Move(path,path+"-original");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"payload"),"failed");
        RetentionMaintenance.Boundary.Value=null;await Assert.ThrowsAsync<InvalidDataException>(()=>RetentionMaintenance.ApplyAsync(store.Settings,plan,true));Assert.True(Directory.Exists(path));
    }
}
