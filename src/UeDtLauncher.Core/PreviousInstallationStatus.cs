using System.Text.Json;
namespace UeDtLauncher;

// Display hint only. Never grants execution, changes the selected release, or certifies payload health.
internal static class PreviousInstallationStatus
{
    internal static async Task<PreviousInstallation?> FindAsync(LauncherConfig config, CancellationToken token = default)
    {
        var selected=config.SelectedRelease;
        if (!config.CatalogAuthenticated || config.AuthenticatedCatalog is null || selected is null || config.VersionedInstallRoot is null) return null;
        try
        {
            var releases=config.AuthenticatedCatalog.Projects.Where(p=>p.ProjectId==selected.ProjectId)
                .SelectMany(p=>p.Releases).Where(r=>r.Environment==selected.Environment && r.Channel==selected.Channel && r.Platform==selected.Platform)
                .Take(257).ToArray();
            if(releases.Length>256 || !releases.Any(r=>r.Version==selected.Version))return null;
            var track=SafePath.ResolveInsideChecked(config.StateRootDir,string.Join('/',selected.ProjectId,selected.Environment,selected.Channel));
            if(!Directory.Exists(track))return null;
            if(Directory.EnumerateDirectories(track).Take(257).Count()>256)return null;
            var candidates=new List<(PreviousInstallation Value,DateTimeOffset Date)>();
            foreach(var release in releases.Where(r=>r.Version!=selected.Version))
            {
                token.ThrowIfCancellationRequested();
                var other=selected with {Version=release.Version};other.Validate();
                var statePath=SafePath.ResolveInsideChecked(config.StateRootDir,other.ReleaseId+"/install-state.json");
                if(!File.Exists(statePath))continue;
                var state=await ReadAsync<InstallState>(statePath,64*1024,token);
                if(state.Version!=other.Version || state.Environment!=other.Environment || state.Channel!=other.Channel || state.Platform!=other.Platform ||
                    string.IsNullOrWhiteSpace(state.ManifestSha256) || !DateTimeOffset.TryParse(state.InstalledAtUtc,out var date))continue;
                var journal=SafePath.ResolveInsideChecked(config.StateRootDir,other.ReleaseId+"/transaction.json");
                if(File.Exists(journal) && (await ReadAsync<UpdateTransactionJournal>(journal,1024*1024,token)).Status!=UpdateTransactionStatus.Committed)continue;
                var manifestPath=SafePath.ResolveInsideChecked(config.StateRootDir,other.ReleaseId+"/installed-manifest.json");
                if(!File.Exists(manifestPath))continue;
                var manifest=await ReadAsync<LauncherManifest>(manifestPath,config.Security.MaxManifestBytes,token);
                LauncherEngine.ValidateManifest(manifest);
                if(manifest.AppId!=other.ProjectId || manifest.Version!=other.Version || manifest.Platform!=other.Platform || manifest.Channel!=other.Channel)continue;
                var app=SafePath.ResolveInsideChecked(config.VersionedInstallRoot,other.ReleaseId);
                if(!File.Exists(SafePath.ResolveInsideChecked(app,manifest.EntryPoint)))continue;
                candidates.Add((new(other,state.InstalledAtUtc),date));
            }
            return candidates.OrderByDescending(c=>c.Date).ThenBy(c=>c.Value.Release.Version,StringComparer.Ordinal).Select(c=>c.Value).FirstOrDefault();
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return null; }
    }
    private static async Task<T> ReadAsync<T>(string path,long limit,CancellationToken token)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>limit)throw new InvalidDataException("Installation metadata is too large.");
        using var output=new MemoryStream();var buffer=new byte[8192];int read;
        while((read=await input.ReadAsync(buffer,token))>0)
        {if(output.Length+read>limit)throw new InvalidDataException("Installation metadata grew.");output.Write(buffer,0,read);}
        return JsonSerializer.Deserialize<T>(output.ToArray(),JsonFiles.Options) ?? throw new InvalidDataException("Empty installation metadata.");
    }
}
