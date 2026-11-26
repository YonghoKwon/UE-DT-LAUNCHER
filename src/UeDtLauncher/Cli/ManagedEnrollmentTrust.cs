using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UeDtLauncher;

internal static class ManagedEnrollmentTrust
{
    internal static string FixedPath(string supplied, string configRoot)
    {
        var expected = Path.GetFullPath(Path.Combine(configRoot, "release-public.pem"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(supplied) || !string.Equals(expected, Path.GetFullPath(supplied), comparison))
            throw new CliArgumentException("Managed enrollment requires release-public.pem in the system Agent configuration directory.");
        return expected;
    }

    internal static FileStream OpenVerified(string path)
    {
        var directories = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var enforceCreationRights = true;
        for (var current = directories; current is not null; current = current.Parent)
        {
            if (!current.Exists || current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint)) RefuseUnsafePath();
            if (OperatingSystem.IsWindows())
            {
                ValidateWindows(current.GetAccessControl(), enforceCreationRights);
                // ProgramData and the volume can allow creating unrelated sibling directories.
                var configRoot = ManagedLauncherPathLayout.SystemDefault().ConfigRoot;
                if (string.Equals(current.FullName, Path.GetDirectoryName(configRoot), StringComparison.OrdinalIgnoreCase)) enforceCreationRights = false;
            }
            else if (OperatingSystem.IsLinux())
            {
                if (RuntimeIdentities.DirectoryOwner(current.FullName) != "0" ||
                    (File.GetUnixFileMode(current.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) RefuseUnsafePath();
            }
            else throw new PlatformNotSupportedException();
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) RefuseUnsafePath();
        var stream = OperatingSystem.IsLinux() ? LinuxFileAccess.OpenRegularRead(path) : File.OpenRead(path);
        try
        {
            if (OperatingSystem.IsWindows()) ValidateWindows(stream.GetAccessControl(), checkWrite: true);
            else if (OperatingSystem.IsLinux())
            {
                var file = LinuxFileAccess.Inspect(stream.SafeFileHandle);
                if (file.Uid != 0 || (file.Mode & (uint)(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) RefuseUnsafePath();
            }
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateWindows(FileSystemSecurity permissions, bool checkWrite)
    {
        var descriptor = new RawSecurityDescriptor(permissions.GetSecurityDescriptorBinaryForm(), 0);
        if (!IsPrivileged(descriptor.Owner) || descriptor.DiscretionaryAcl is null) RefuseUnsafePath();
        var dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (checkWrite) dangerous |= FileSystemRights.Write;
        const int genericAll = 0x10000000, genericWrite = 0x40000000;
        var mask = (int)dangerous | genericAll | (checkWrite ? genericWrite : 0);
        foreach (GenericAce entry in descriptor.DiscretionaryAcl!)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not QualifiedAce) RefuseUnsafePath();
            if (entry is QualifiedAce allowed && allowed.AceQualifier == AceQualifier.AccessAllowed &&
                (allowed.AccessMask & mask) != 0 &&
                !IsPrivileged(allowed.SecurityIdentifier)) RefuseUnsafePath();
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsPrivileged(SecurityIdentifier? identity) => identity is not null &&
        (identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || identity.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
         identity.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller

    private static void RefuseUnsafePath() => throw new UnauthorizedAccessException("Managed enrollment signing key access is not administrator-protected.");
}
