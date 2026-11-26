using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher;

[SupportedOSPlatform("linux")]
public static class LinuxFileAccess
{
    public readonly record struct Identity(ulong Device, ulong Inode, uint Uid, uint Gid, uint Mode);
    public sealed record Metadata(Identity File, byte[]? PosixAcl, byte[]? SecurityLabel)
    {
        public bool HasSameAccess(Metadata other) => File.Uid == other.File.Uid && File.Gid == other.File.Gid &&
            File.Mode == other.File.Mode && SameAttribute(PosixAcl, other.PosixAcl) && SameAttribute(SecurityLabel, other.SecurityLabel);
    }

    private const uint TypeMask = 0xf000, RegularFile = 0x8000;
    private const long LinuxX64Fstat = 5;
    private const string AccessAcl = "system.posix_acl_access", SelinuxLabel = "security.selinux";

    public static FileStream OpenRegularRead(string path)
    {
        // O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK keeps links and FIFOs out of the validation path.
        var handle = new SafeFileHandle((IntPtr)open(path, 0x20000 | 0x80000 | 0x800), ownsHandle: true);
        try
        {
            if (handle.IsInvalid) throw NativeError();
            if ((Inspect(handle).Mode & TypeMask) != RegularFile) throw new IOException("Expected a regular file.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    public static Identity Inspect(SafeFileHandle handle)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Linux file metadata requires x64.");
        // Linux x86-64 struct stat. Use an oversized buffer, as in the credential reader.
        var data = Marshal.AllocHGlobal(256);
        try
        {
            // SYS_fstat keeps the Linux x64 stat layout on glibc 2.28, which has no public fstat symbol.
            if (FstatSyscall(LinuxX64Fstat, handle, data) != 0) throw NativeError();
            return new(unchecked((ulong)Marshal.ReadInt64(data, 0)), unchecked((ulong)Marshal.ReadInt64(data, 8)),
                unchecked((uint)Marshal.ReadInt32(data, 28)), unchecked((uint)Marshal.ReadInt32(data, 32)), unchecked((uint)Marshal.ReadInt32(data, 24)));
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public static Metadata ReadMetadata(SafeFileHandle handle) => new(Inspect(handle), ReadAttribute(handle, AccessAcl), ReadAttribute(handle, SelinuxLabel));

    public static void PreserveAccess(SafeFileHandle target, Metadata source)
    {
        var current = Inspect(target);
        if ((current.Uid != source.File.Uid || current.Gid != source.File.Gid) && fchown(target, source.File.Uid, source.File.Gid) != 0)
            throw NativeError();
        if (fchmod(target, source.File.Mode & 0xfff) != 0) throw NativeError();
        MatchAttribute(target, AccessAcl, source.PosixAcl);
        MatchAttribute(target, SelinuxLabel, source.SecurityLabel);
        if (!source.HasSameAccess(ReadMetadata(target))) throw new IOException("Could not preserve Linux file ownership or access metadata.");
    }

    private static byte[]? ReadAttribute(SafeFileHandle handle, string name)
    {
        var length = fgetxattr(handle, name, null, 0);
        if (length == -1 && Marshal.GetLastPInvokeError() == 61) return null; // ENODATA
        if (length < 0) throw NativeError();
        if (length > 65536) throw new IOException("Linux access attribute exceeds the supported size.");
        var value = new byte[(int)length];
        if (fgetxattr(handle, name, value, (nuint)value.Length) != length) throw NativeError();
        return value;
    }

    private static void MatchAttribute(SafeFileHandle handle, string name, byte[]? expected)
    {
        if (SameAttribute(ReadAttribute(handle, name), expected)) return;
        var result = expected is null ? fremovexattr(handle, name) : fsetxattr(handle, name, expected, (nuint)expected.Length, 0);
        if (result != 0) throw NativeError();
    }

    private static bool SameAttribute(byte[]? first, byte[]? second) => first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
    private static IOException NativeError() => new("Cannot inspect or preserve Linux file access metadata.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", EntryPoint = "syscall", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern long FstatSyscall(long number, SafeFileHandle fd, IntPtr data);
    [DllImport("libc", SetLastError = true)] private static extern int fchown(SafeFileHandle fd, uint uid, uint gid);
    [DllImport("libc", SetLastError = true)] private static extern int fchmod(SafeFileHandle fd, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern nint fgetxattr(SafeFileHandle fd, string name, [Out] byte[]? value, nuint size);
    [DllImport("libc", SetLastError = true)] private static extern int fsetxattr(SafeFileHandle fd, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fremovexattr(SafeFileHandle fd, string name);
}
