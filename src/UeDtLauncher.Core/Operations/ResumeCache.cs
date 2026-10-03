using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UeDtLauncher;

public sealed record ResumeRecord(int SchemaVersion,string ReleaseId,string ManifestSha256,string Owner,string Session,long BudgetBytes);

/// <summary>Trusted requester partition and installation-wide content quota. Not a trust source.</summary>
internal sealed class ResumeCache
{
    private readonly string root;
    private readonly long budget;
    private readonly object gate=new();
    private readonly Dictionary<string,long> sizes=new(SafePath.FileSystemComparer);
    private readonly Dictionary<string,long> reservations=new(SafePath.FileSystemComparer);
    private ResumeCache(string root,long budget,Dictionary<string,long> stored){this.root=root;this.budget=budget;foreach(var item in stored)sizes.Add(item.Key,item.Value);}
    internal static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static ResumeCache? Open(LauncherConfig config,ManifestDocument document)
    {
        var budget=config.Performance.ResumeCacheBytes;
        if(budget is null or <=0 || !document.SignatureVerified || config.SelectedRelease is null)return null;
        if(document.Manifest.Files.Count>0 && document.Manifest.Files.All(file=>file.Size>budget))return null;
        var owner=config.TrustedOperationOwner;
        if(owner is null){if(config.IsManagedDeployment)return null;owner=RuntimeIdentities.Current();}
        var parent=Path.Combine(Path.GetDirectoryName(config.StagingDir)!,"resume-cache");
        SafePath.EnsureNoReparsePoints(Path.GetFullPath(config.StateRootDir),parent);
        var stored=new Dictionary<string,long>(SafePath.FileSystemComparer);
        if(Directory.Exists(parent))
        {
            var pending=new Stack<string>();pending.Push(parent);
            while(pending.TryPop(out var directory))
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    SafePath.EnsureNoReparsePoints(Path.GetFullPath(config.StateRootDir),path);
                    if(Directory.Exists(path))pending.Push(path);
                    else if(Path.GetFileName(path)!="resume.json")stored.Add(path,new FileInfo(path).Length);
                    if(stored.Count+pending.Count>config.Security.MaxManifestFiles*4L)return null;
                }
            }
        }
        var digest=Digest(document.Json);
        var root=SafePath.ResolveInsideChecked(parent,"v2/"+Digest(owner.Owner+"\0"+owner.Session)+"/"+digest);
        var record=SafePath.ResolveInsideChecked(root,"resume.json");
        var expected=new ResumeRecord(2,config.SelectedRelease.ReleaseId,digest,owner.Owner,owner.Session,budget.Value);
        if(File.Exists(record))
        {
            if(new FileInfo(record).Length>65536)return null;
            ResumeRecord? actual;
            try{actual=JsonSerializer.Deserialize<ResumeRecord>(File.ReadAllText(record),JsonFiles.Options);}catch(JsonException){return null;}
            if(actual is null || actual with{BudgetBytes=expected.BudgetBytes}!=expected)return null;
            if(actual.BudgetBytes!=expected.BudgetBytes)RuntimeStatePersistence.Write(record,expected);
        }
        else RuntimeStatePersistence.Write(record,expected);
        var cache=new ResumeCache(root,budget.Value,stored);
        foreach(var path in Directory.EnumerateFiles(root,"*.new")){try{File.Delete(path);cache.Refresh(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
        if(cache.AccountedBytes>budget)return null;
        return cache;
    }
    internal string Partial(ManifestFile file)=>SafePath.ResolveInsideChecked(root,Digest(file.Path)+".partial");
    private string Complete(ManifestFile file)=>SafePath.ResolveInsideChecked(root,Digest(file.Path)+".verified");
    internal Reservation? ReservePartial(ManifestFile file)=>Reserve(Partial(file),file.Size);
    private Reservation? Reserve(string path,long maximum)
    {
        lock(gate)
        {
            Refresh(path);
            if(reservations.ContainsKey(path))throw new InvalidOperationException("Duplicate cache writer.");
            var used=sizes.Values.Sum()+reservations.Sum(item=>Math.Max(0,item.Value-sizes.GetValueOrDefault(item.Key)));
            if(checked(used+Math.Max(0,maximum-sizes.GetValueOrDefault(path)))>budget)return null;
            reservations.Add(path,maximum);return new(this,path);
        }
    }
    private void Refresh(string path){if(File.Exists(path))sizes[path]=new FileInfo(path).Length;else sizes.Remove(path);}
    internal long AccountedBytes{get{lock(gate)return sizes.Values.Sum()+reservations.Sum(item=>Math.Max(0,item.Value-sizes.GetValueOrDefault(item.Key)));}}
    internal sealed class Reservation(ResumeCache cache,string path):IDisposable
    {
        internal string Path=>path;
        public void Dispose(){lock(cache.gate){cache.Refresh(path);cache.reservations.Remove(path);}}
    }
    internal async Task<bool> TryCopyAsync(ManifestFile file,string staging,CancellationToken token)
    {
        var source=Complete(file);var created=false;
        try
        {
            if(!File.Exists(source))return false;
            if(new FileInfo(source).Length!=file.Size || !await Hashing.Sha256MatchesAsync(source,file.Sha256,token))
            {File.Delete(source);lock(gate)Refresh(source);return false;}
            File.Copy(source,staging,false);created=true;
            if(await Hashing.Sha256MatchesAsync(staging,file.Sha256,token))return true;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        if(created)try{File.Delete(staging);}catch(IOException){}catch(UnauthorizedAccessException){}
        return false;
    }
    internal async Task StoreAsync(ManifestFile file,string staging,CancellationToken token)
    {
        var target=Complete(file);var temporary=target+".new";
        using var reserved=Reserve(temporary,file.Size);if(reserved is null)return;
        try
        {
            File.Copy(staging,temporary,false);
            if(!await Hashing.Sha256MatchesAsync(temporary,file.Sha256,token))throw new InvalidDataException("Resume copy changed.");
            File.Move(temporary,target,true);
            lock(gate){Refresh(target);Refresh(temporary);}
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        finally{if(File.Exists(temporary))try{File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
