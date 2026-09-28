using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher;

// Managed provisioning is privileged; the service only reads. Never create a plaintext,
// broadly-readable file and subsequently tighten its permissions.
internal static class ProtectedCredentialFile
{
    private const int NoFollow = 0x20000, CloseOnExec = 0x80000, DirectoryFlag = 0x10000;
    private const int Create = 0x40, Exclusive = 0x80, WriteOnly = 1;
    private const int MaxBytes = 64 * 1024;

    public static void Write(string path, byte[] plain, bool managed, bool replace)
    {
        if (plain.Length > MaxBytes) throw new InvalidDataException("Credential exceeds size limit.");
        if (OperatingSystem.IsWindows()) { WriteWindows(path, plain, managed, replace); return; }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Protected credentials support Windows and Linux.");
        var owner = LinuxOwner(managed);
        if (managed && geteuid() != 0) throw new UnauthorizedAccessException("Managed provisioning requires root.");
        EnsureLinuxDirectory(Path.GetDirectoryName(path)!, managed, owner.Gid);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, managed);
        var name = Path.GetFileName(path);
        var temporary = ".credential-" + Guid.NewGuid().ToString("N");
        try
        {
            var descriptor = openat(Fd(parent), temporary, WriteOnly | Create | Exclusive | NoFollow | CloseOnExec, 0x180); // 0600
            using (var handle = Handle(descriptor))
            using (var stream = new FileStream(handle, FileAccess.Write))
            {
                if (fchown(descriptor, owner.Uid, owner.Gid) != 0 || fchmod(descriptor, 0x180) != 0) ThrowNative();
                stream.Write(plain); stream.Flush(true);
            }
            if (!replace)
            {
                // linkat is atomic no-replace; the temporary name is removed immediately below.
                if (linkat(Fd(parent), temporary, Fd(parent), name, 0) != 0) ThrowNative();
            }
            else
            {
                if (File.Exists(path)) { using var previous = OpenLinux(parent, name, owner.Uid); }
                if (renameat(Fd(parent), temporary, Fd(parent), name) != 0) ThrowNative();
            }
            if (fsync(Fd(parent)) != 0) ThrowNative();
        }
        finally { _ = unlinkat(Fd(parent), temporary, 0); }
    }

    public static byte[] Read(string path, bool managed)
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(path, managed);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var owner = LinuxOwner(managed);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, managed);
        using var handle = OpenLinux(parent, Path.GetFileName(path), owner.Uid);
        using var stream = new FileStream(handle, FileAccess.Read);
        return ReadBounded(stream);
    }

    public static string RepairLinux(string path, bool managed, bool apply)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Permission repair is Linux-only.");
        var owner = LinuxOwner(managed);
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, managed);
        using var handle = Handle(openat(Fd(parent), Path.GetFileName(path), NoFollow | CloseOnExec, 0));
        var stat = Stat(Fd(handle));
        if ((stat.Mode & 0xf000) != 0x8000 || stat.Links != 1) throw new UnauthorizedAccessException("Credential must be a regular, unlinked file.");
        if (apply)
        {
            if (geteuid() != 0 && (managed || stat.Uid != geteuid())) throw new UnauthorizedAccessException("Permission repair requires the owner or root.");
            if (fchown(Fd(handle), owner.Uid, owner.Gid) != 0 || fchmod(Fd(handle), 0x180) != 0) ThrowNative();
        }
        return $"{(apply ? "Applied" : "Planned")}: credential owner UID {owner.Uid}, GID {owner.Gid}, mode 0600. No parent or other file changed.";
    }

    private static byte[] ReadBounded(Stream stream)
    {
        if (stream.Length is < 1 or > MaxBytes) throw new InvalidDataException("Invalid credential size.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) { CryptographicOperations.ZeroMemory(bytes); throw new IOException("Credential changed while reading."); }
        return bytes;
    }

    private static (uint Uid, uint Gid) LinuxOwner(bool managed)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Linux credential ownership currently requires x64.");
        if (!managed) return (geteuid(), getegid());
        var user = getpwnam("uedt");
        if (user == IntPtr.Zero) throw new UnauthorizedAccessException("Service account uedt is missing; install/provision the service before registering credentials.");
        return (unchecked((uint)Marshal.ReadInt32(user, 16)), unchecked((uint)Marshal.ReadInt32(user, 20)));
    }

    private static void EnsureLinuxDirectory(string path, bool managed, uint gid)
    {
        CheckAncestors(path);
        if (Directory.Exists(path)) return;
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Credential parent must be provisioned first.");
        if (mkdir(path, managed ? 0x1e8u : 0x1c0u) != 0) ThrowNative(); // 0750 / 0700
        using var handle = Handle(open(path, NoFollow | CloseOnExec | DirectoryFlag, 0));
        if (managed && (fchown(Fd(handle), 0, gid) != 0 || fchmod(Fd(handle), 0x1e8) != 0)) ThrowNative();
    }

    private static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new UnauthorizedAccessException("Credential path may not contain links.");
            if (OperatingSystem.IsLinux() && current.Exists)
            {
                using var handle = Handle(open(current.FullName, NoFollow | CloseOnExec | DirectoryFlag, 0));
                var stat = Stat(Fd(handle));
                if (stat.Uid != 0 && stat.Uid != geteuid()) throw new UnauthorizedAccessException("Credential ancestor has an unexpected owner.");
                if ((stat.Mode & 0x12) != 0 && (stat.Mode & 0x200) == 0) // writable by others without sticky protection
                    throw new UnauthorizedAccessException("Credential ancestor is writable by other identities.");
            }
        }
    }

    private static SafeFileHandle OpenDirectory(string path, bool managed)
    {
        CheckAncestors(path);
        var handle = Handle(open(path, NoFollow | CloseOnExec | DirectoryFlag, 0));
        var stat = Stat(Fd(handle));
        if (stat.Uid != (managed ? 0 : geteuid()) || (stat.Mode & 0x12) != 0)
        { handle.Dispose(); throw new UnauthorizedAccessException("Credential directory ownership/permissions are unsafe."); }
        return handle;
    }

    private static SafeFileHandle OpenLinux(SafeFileHandle parent, string name, uint uid)
    {
        var handle = Handle(openat(Fd(parent), name, NoFollow | CloseOnExec, 0));
        var stat = Stat(Fd(handle));
        if (stat.Uid != uid || (stat.Mode & 0xfff) != 0x180 || (stat.Mode & 0xf000) != 0x8000 || stat.Links != 1)
        { handle.Dispose(); throw new UnauthorizedAccessException("Credential must be service/owner-owned, mode 0600 and a regular file with one link."); }
        return handle;
    }

    private static (uint Mode, uint Uid, ulong Links) Stat(int fd)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException();
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (fstat(fd, buffer) != 0) ThrowNative();
            return (unchecked((uint)Marshal.ReadInt32(buffer, 24)), unchecked((uint)Marshal.ReadInt32(buffer, 28)), unchecked((ulong)Marshal.ReadInt64(buffer, 16)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] WindowsIdentities(bool managed) => managed
        ? [new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.LocalServiceSid, null)]
        : [WindowsIdentity.GetCurrent().User!, new(WellKnownSidType.BuiltinAdministratorsSid, null), new(WellKnownSidType.LocalSystemSid, null)];

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileAcl(bool managed)
    {
        var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
        var identities = WindowsIdentities(managed); acl.SetOwner(identities[0]);
        foreach (var sid in identities) acl.AddAccessRule(new FileSystemAccessRule(sid,
            managed && sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ? FileSystemRights.Read : FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsAcl(FileSystemSecurity acl, bool managed, bool directory)
    {
        var allowed = WindowsIdentities(managed).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner.Value))
            throw new UnauthorizedAccessException("Unexpected credential owner.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var dangerous = directory ? FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership : FileSystemRights.FullControl;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & dangerous) != 0 && !allowed.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("Credential ACL grants access to an unrelated identity.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, byte[] plain, bool managed, bool replace)
    {
        if (managed && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Managed credential provisioning requires an elevated administrator.");
        var root = Path.GetDirectoryName(path)!; CheckAncestors(root);
        if (!Directory.Exists(root))
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            var identities = WindowsIdentities(managed); acl.SetOwner(identities[0]);
            foreach (var sid in identities) acl.AddAccessRule(new FileSystemAccessRule(sid,
                managed && sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(acl);
        }
        ValidateWindowsAcl(new DirectoryInfo(root).GetAccessControl(), managed, true);
        if (File.Exists(path)) { _ = ReadWindows(path, managed); if (!replace) throw new IOException("Credential already exists."); }
        if (new FileInfo(path).LinkTarget is not null) throw new UnauthorizedAccessException("Credential links are forbidden.");
        var temporary = Path.Combine(root, ".credential-" + Guid.NewGuid().ToString("N"));
        var stored = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
        try
        {
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, FileAcl(managed)))
            { stream.Write(stored); stream.Flush(true); }
            File.Move(temporary, path, replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); CryptographicOperations.ZeroMemory(stored); }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadWindows(string path, bool managed)
    {
        CheckAncestors(Path.GetDirectoryName(path)!);
        ValidateWindowsAcl(new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl(), managed, true);
        if (new FileInfo(path).LinkTarget is not null || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("Credential links are forbidden.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateWindowsAcl(stream.GetAccessControl(), managed, false);
        var stored = ReadBounded(stream);
        try { return ProtectedData.Unprotect(stored, null, DataProtectionScope.LocalMachine); }
        finally { CryptographicOperations.ZeroMemory(stored); }
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static SafeFileHandle Handle(int fd) { if (fd < 0) ThrowNative(); return new SafeFileHandle((IntPtr)fd, true); }
    private static void ThrowNative() => throw new IOException("Protected credential filesystem operation failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int openat(int dir, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdir(string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int fstat(int fd, IntPtr buffer);
    [DllImport("libc", SetLastError = true)] private static extern int fchmod(int fd, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int fchown(int fd, uint uid, uint gid);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int linkat(int oldDir, string oldPath, int newDir, string newPath, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat(int oldDir, string oldPath, int newDir, string newPath);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dir, string path, int flags);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc")] private static extern uint getegid();
    [DllImport("libc")] private static extern IntPtr getpwnam(string name);
}
