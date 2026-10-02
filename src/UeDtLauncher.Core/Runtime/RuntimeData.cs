using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace UeDtLauncher;

public sealed class RuntimeDataOptions
{
    public bool Enabled { get; set; }
    public string Adapter { get; set; } = "unreal-engine";
    public string Policy { get; set; } = "per-user-per-release";
    // Optional absolute, administrator-configured root. No environment/template expansion.
    public string? RootDirectory { get; set; }
}

public sealed record RuntimeDataPlan(string Adapter, string Policy, string? RootDirectory,
    string ReleaseId, string AttemptId, string Owner, string[] ProtectedDirectories, string InstallationId);
public sealed record RuntimeDataPaths(string Root, string UserDirectory, string LogFile);
public sealed class RuntimeDataException(Exception? inner = null) : InvalidOperationException(
    "프로그램 저장 경로를 준비할 수 없습니다. 런처 설정과 폴더 권한을 관리자에게 확인해 주세요.", inner);

/// <summary>Plans are frozen with the launch ticket; only the authenticated user host creates data.</summary>
public static class RuntimeDataPolicy
{
    public const string Capability = "runtime-data-v1";

    public static void ValidateConfiguration(LauncherConfig config)
    {
        var options = config.RuntimeData;
        if (options?.Enabled != true) return;
        if (config.SchemaVersion != 3 || options.Adapter != "unreal-engine" || options.Policy != "per-user-per-release" ||
            string.IsNullOrWhiteSpace(config.DistributionServerUrl))
            throw new RuntimeDataException();
        if (config.LaunchArguments?.Any(IsReservedArgument) == true || config.LaunchArguments is { Length: > 250 })
            throw new RuntimeDataException();
        if (options.RootDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Path.IsPathFullyQualified(options.RootDirectory)) throw new RuntimeDataException();
            ValidateSeparation(Path.GetFullPath(options.RootDirectory), ProtectedDirectories(config));
            CheckLinks(options.RootDirectory);
        }
    }

    private static bool IsReservedArgument(string argument) => argument is null ||
        argument.StartsWith("-UserDir", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith("-abslog", StringComparison.OrdinalIgnoreCase) || argument.Contains('\0');

    private static string[] ProtectedDirectories(LauncherConfig c)
    {
        var paths = new[] { c.VersionedInstallRoot ?? c.InstallDir, c.InstallDir, c.StateRootDir,
            c.StagingDir, c.BackupDir, c.LogDir, Path.GetDirectoryName(Path.GetFullPath(c.InstallStatePath))!,
            ManagedLauncherPathLayout.Current().CredentialRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UE-DT Launcher", "credentials") };
        return paths.Select(Path.GetFullPath).Distinct(SafePath.FileSystemComparer).ToArray();
    }

    internal static RuntimeDataPlan? Plan(LauncherConfig config, RuntimeIdentity requester, string attempt)
    {
        ValidateConfiguration(config);
        if (config.RuntimeData?.Enabled != true) return null;
        var selection = config.SelectedRelease ?? throw new RuntimeDataException();
        selection.Validate();
        return new(config.RuntimeData.Adapter, config.RuntimeData.Policy, config.RuntimeData.RootDirectory,
            selection.ReleaseId, attempt, requester.Owner, ProtectedDirectories(config), RuntimeStore.InstallationId(config));
    }

    internal static void ValidatePlan(RuntimeDataPlan plan)
    {
        if (plan.Adapter != "unreal-engine" || plan.Policy != "per-user-per-release" ||
            !Guid.TryParseExact(plan.AttemptId, "N", out _) || string.IsNullOrWhiteSpace(plan.Owner) ||
            plan.ProtectedDirectories is null || plan.ProtectedDirectories.Length is < 1 or > 32 ||
            plan.ProtectedDirectories.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)) ||
            plan.InstallationId is not { Length: 64 } || !plan.InstallationId.All(Uri.IsHexDigit)) throw new RuntimeDataException();
        var parts = plan.ReleaseId?.Split('/') ?? [];
        if (parts.Length != 5) throw new RuntimeDataException();
        try { new ReleaseSelection(parts[0], parts[1], parts[2], parts[4], parts[3]).Validate(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException) { throw new RuntimeDataException(ex); }
        if (plan.RootDirectory is not null && !Path.IsPathFullyQualified(plan.RootDirectory)) throw new RuntimeDataException();
    }

    public static RuntimeDataPaths Resolve(RuntimeDataPlan plan)
    {
        ValidatePlan(plan);
        var root = plan.RootDirectory ?? DefaultRoot();
        root = Path.GetFullPath(root);
        ValidateSeparation(root, plan.ProtectedDirectories);
        // Configured test/service roots still partition by the actual authenticated identity.
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.Owner))).ToLowerInvariant();
        var release = SafePath.ResolveInside(root, "users/" + owner + "/releases/" + plan.ReleaseId);
        return new(root, Path.Combine(release, "user"), Path.Combine(release, "logs", plan.AttemptId + ".log"));
    }

    private static string DefaultRoot()
    {
        var basePath = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) :
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        if (!Path.IsPathFullyQualified(basePath)) throw new RuntimeDataException();
        return Path.Combine(basePath, "UE-DT Launcher", "RuntimeData");
    }

    internal static void InspectUserRootReadOnly(LauncherConfig config)
    {
        ValidateConfiguration(config);
        var root = Path.GetFullPath(config.RuntimeData?.RootDirectory ?? DefaultRoot());
        ValidateSeparation(root, ProtectedDirectories(config)); CheckLinks(root);
        var existing = new DirectoryInfo(root);
        while (!existing.Exists) existing = existing.Parent ?? throw new RuntimeDataException();
        ValidateDirectory(existing.FullName, RuntimeIdentities.Current().Owner);
    }

    internal static void ValidateSeparation(string root, IEnumerable<string> protectedPaths)
    {
        foreach (var p in protectedPaths)
        {
            var path = Path.GetFullPath(p);
            if (SafePath.IsInside(root, path, SafePath.FileSystemComparison) || SafePath.IsInside(path, root, SafePath.FileSystemComparison))
                throw new RuntimeDataException();
        }
    }

    internal static void CheckLinks(string path)
    {
        for (var p = new DirectoryInfo(Path.GetFullPath(path)); p is not null; p = p.Parent)
            if (p.LinkTarget is not null || (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)) throw new RuntimeDataException();
    }

    // Called in runtime-host after Agent authentication, NOT in GUI or service-account preflight.
    internal static RuntimeHostRequest PrepareHost(RuntimeHostRequest request, RuntimeIdentity host)
    {
        if (request.RuntimeData is not { } plan) return request;
        try
        {
            if (host.Owner != plan.Owner || !RuntimeIdentities.StillMatches(host)) throw new RuntimeDataException();
            if (request.Arguments.Any(IsReservedArgument)) throw new RuntimeDataException();
            var paths = Resolve(plan);
            EnsurePrivateDirectory(paths.UserDirectory, paths.Root, host.Owner);
            EnsurePrivateDirectory(Path.GetDirectoryName(paths.LogFile)!, paths.Root, host.Owner);
            // Demonstrate actual write access; do not silently fall back to the install folder.
            var probe = Path.Combine(paths.UserDirectory, ".write-check-" + plan.AttemptId);
            using (var output = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Flush(true);
            File.Delete(probe);
            return request with { Arguments = [.. request.Arguments, "-UserDir=" + paths.UserDirectory.Replace('\\', '/') + "/",
                "-abslog=" + paths.LogFile.Replace('\\', '/')], RuntimeData = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new RuntimeDataException(ex); }
    }

    private static void EnsurePrivateDirectory(string path, string root, string owner)
    {
        CheckLinks(path);
        var missing = new Stack<string>();
        var current = new DirectoryInfo(path);
        while (!current.Exists) { missing.Push(current.FullName); current = current.Parent ?? throw new RuntimeDataException(); }
        ValidateDirectory(current.FullName, owner);
        while (missing.TryPop(out var next))
        {
            if (OperatingSystem.IsWindows()) CreateWindowsDirectory(next, owner);
            else if (OperatingSystem.IsLinux()) Directory.CreateDirectory(next, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else throw new PlatformNotSupportedException();
            CheckLinks(next); ValidateDirectory(next, owner);
        }
        // Validate every existing path in our namespace, not just the final leaf.
        for (var p = new DirectoryInfo(path); p is not null && SafePath.IsInside(root, p.FullName, SafePath.FileSystemComparison); p = p.Parent)
            ValidateDirectory(p.FullName, owner);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path, string owner)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var sid = new SecurityIdentifier(owner); security.SetOwner(sid);
        foreach (var allowed in new[] { sid, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(allowed, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    internal static void ValidateDirectory(string path, string owner)
    {
        CheckLinks(path);
        var actualOwner = RuntimeIdentities.DirectoryOwner(path);
        if (OperatingSystem.IsWindows()) ValidateWindowsDirectory(path, owner, actualOwner);
        else if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(path);
            if (actualOwner != owner && actualOwner != "0") throw new RuntimeDataException();
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 && (mode & UnixFileMode.StickyBit) == 0) throw new RuntimeDataException();
        }
        else throw new PlatformNotSupportedException();
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsDirectory(string path, string owner, string actualOwner)
    {
        var allowed = new HashSet<string> { owner, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
        if (!allowed.Contains(actualOwner)) throw new RuntimeDataException();
        var dangerous = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & dangerous) != 0 && !allowed.Contains(rule.IdentityReference.Value))
                throw new RuntimeDataException();
    }
}
