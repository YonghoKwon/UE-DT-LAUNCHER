namespace UeDtLauncher;

/// <summary>Selection-only client input. Operational paths, authentication and launch arguments stay in the Agent.</summary>
public static class ManagedClientContext
{
    public static string UserLogRoot(string displayPath)
    {
        var id=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(displayPath)))).ToLowerInvariant()[..16];
        return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),"UE-DT Launcher","ui-logs",id);
    }
    public static LauncherConfig Create(LauncherConfig display)
    {
        if(!display.IsManagedDeployment)throw new InvalidDataException("A managed client configuration is required.");
        if(display.SchemaVersion is <1 or >3)throw new InvalidDataException("Unsupported client configuration schema.");
        if(!string.IsNullOrWhiteSpace(display.ProjectId))ReleaseSidecar.Segment(display.ProjectId);
        KnownValues.ValidateReleaseTuple(display.TargetPlatform,display.Environment,display.Channel);
        if(display.VersionPolicy is not ("latest" or "exact"))throw new InvalidDataException("Invalid client version policy.");
        if(display.RequestedVersion is not null)ReleaseSidecar.Segment(display.RequestedVersion);
        return new()
        {
            SchemaVersion=display.SchemaVersion,DeploymentMode="managed-agent",ManifestUrl="",IsManagedClientContext=true,
            ProjectId=string.IsNullOrWhiteSpace(display.ProjectId)?null:display.ProjectId,ClientProfile=display.ClientProfile,Environment=display.Environment,
            Channel=display.Channel,VersionPolicy=display.VersionPolicy,RequestedVersion=display.RequestedVersion,
            TargetPlatform=display.TargetPlatform,LaunchAfterUpdate=display.LaunchAfterUpdate,RepairMode=display.RepairMode
        };
    }
    public static void Bind(LauncherConfig client,ReleaseSelection selection)
    {
        selection.Validate();
        if(!client.IsManagedDeployment || client.ProjectId!=selection.ProjectId || client.Environment!=selection.Environment ||
           client.Channel!=selection.Channel || client.TargetPlatform!=selection.Platform)
            throw new InvalidDataException("Agent returned another managed release.");
        client.SelectedRelease=selection;client.VersionPolicy="exact";client.RequestedVersion=selection.Version;
    }
}

public sealed record ManagedShortcutOptions(bool Desktop,bool StartMenu,bool Registration,string Name,string Publisher,string? IconRelativePath);

public sealed record ManagedClientPresentation(ReleaseSelection? Selection,string InstallationId,string? InstallDirectory,
    string DisplayName,ManagedShortcutOptions Shortcuts)
{
    public const string Capability="client-presentation-v1";
    public void Validate(ReleaseSelection? expected=null,string? installationId=null)
    {
        if(expected is not null && Selection!=expected)throw new InvalidDataException("Agent presentation release mismatch.");
        Selection?.Validate();
        if(InstallationId.Length!=64 || !InstallationId.All(char.IsAsciiHexDigit) || (installationId is not null && InstallationId!=installationId))
            throw new InvalidDataException("Agent presentation installation mismatch.");
        if(InstallDirectory is not null && (!Path.IsPathFullyQualified(InstallDirectory) || Path.GetFullPath(InstallDirectory)!=InstallDirectory))
            throw new InvalidDataException("Invalid managed installation folder.");
        ValidateLabel(Shortcuts.Name);ValidateLabel(Shortcuts.Publisher);
        if(Shortcuts.IconRelativePath is not null && InstallDirectory is not null)SafePath.ResolveInsideChecked(InstallDirectory,Shortcuts.IconRelativePath);
    }
    internal static void ValidateLabel(string label)
    {
        if(string.IsNullOrWhiteSpace(label)||label.Length>128||label is "." or ".."||label.Any(c=>char.IsControl(c)||"\\/:*?\"<>|".Contains(c)))
            throw new InvalidDataException("Invalid shortcut label.");
    }
    public static ManagedClientPresentation FromAgent(LauncherConfig config,bool installed)
    {
        var options=config.WindowsIntegration;
        var directory=installed?Path.GetFullPath(config.InstallDir):null;
        string? icon=null;
        if(directory is not null && !string.IsNullOrWhiteSpace(options.IconPath))
        {
            try {var relative=Path.GetRelativePath(directory,Path.GetFullPath(options.IconPath));if(File.Exists(SafePath.ResolveInsideChecked(directory,relative)))icon=relative;}
            catch(Exception e)when(e is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException){ }
        }
        var name=options.ShortcutName??options.AppName;
        if(string.IsNullOrWhiteSpace(name))name=config.ProjectId??"UE-DT";
        var result=new ManagedClientPresentation(config.SelectedRelease,RuntimeStore.InstallationId(config),directory,
            options.AppName??config.ProjectId??"UE-DT",new(options.CreateDesktopShortcut,options.CreateStartMenuShortcut,options.RegisterAppEntry,name,options.Publisher,icon));
        result.Validate(config.SelectedRelease);return result;
    }
}
