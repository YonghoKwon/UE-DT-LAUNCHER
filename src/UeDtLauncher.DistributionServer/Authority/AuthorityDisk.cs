using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher.Distribution.Authority;

internal enum AuthorityBoundary
{
    GenerationWrite, GenerationFlush, GenerationRename, GenerationDirectoryFlush,
    HeadWrite, HeadFlush, BeforeHeadRename, HeadRename, HeadDirectoryFlush, AuditAppend
}

internal static class AuthorityDisk
{
    internal static readonly AsyncLocal<Action<AuthorityBoundary>?> Boundary = new();
    internal static readonly AsyncLocal<int?> WindowsRenameError = new();
    private const int AtCurrentDirectory = -100;
    private const int RenameNoReplace = 1;
    private const int FileRenameInfoEx = 22;
    private const int ReplaceIfExistsAndPosixSemantics = 3;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;

    internal static FileStream OpenReader(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    internal static void Reach(AuthorityBoundary point) => Boundary.Value?.Invoke(point);

    internal static string Temporary(string path) => path + ".tmp-" + Guid.NewGuid().ToString("N");

    internal static void Write(string path, byte[] bytes, bool head = false)
    {
        var temporary = Temporary(path);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                Reach(head ? AuthorityBoundary.HeadWrite : AuthorityBoundary.GenerationWrite);
                file.Flush(true);
                Reach(head ? AuthorityBoundary.HeadFlush : AuthorityBoundary.GenerationFlush);
            }
            if (head) Reach(AuthorityBoundary.BeforeHeadRename);
            Rename(temporary, path, replace: head);
        }
        finally { TryDelete(temporary); }

        if (!head)
        {
            Reach(AuthorityBoundary.GenerationRename);
            FlushDirectory(Path.GetDirectoryName(path)!);
            Reach(AuthorityBoundary.GenerationDirectoryFlush);
        }
    }

    internal static void CompleteHeadPublication(string directory, long generation)
    {
        try
        {
            Reach(AuthorityBoundary.HeadRename);
            FlushDirectory(directory);
            Reach(AuthorityBoundary.HeadDirectoryFlush);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new AuthorityCommitDurabilityException(generation, error);
        }
    }

    internal static void Rename(string source, string destination, bool replace = false, bool directory = false)
    {
        if (OperatingSystem.IsLinux())
        {
            var result = replace ? RenameLinux(source, destination)
                : RenameAt2(AtCurrentDirectory, source, AtCurrentDirectory, destination, RenameNoReplace);
            if (result != 0) throw NativeError("Authority rename", Marshal.GetLastPInvokeError());
            return;
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authority storage requires Windows or Linux.");
        if (replace) ReplaceWindows(source, destination);
        else if (directory) Directory.Move(source, destination);
        else File.Move(source, destination, overwrite: false);
    }

    private static void ReplaceWindows(string source, string destination)
    {
        using var handle = CreateFile(ExtendedPath(source), DeleteAccess, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError("Open authority rename source", Marshal.GetLastPInvokeError());

        var name = Encoding.Unicode.GetBytes(ExtendedPath(destination) + "\0");
        // FILE_RENAME_INFO stores a DWORD, an aligned HANDLE, a DWORD, then a WCHAR array.
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var size = nameOffset + name.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, ReplaceIfExistsAndPosixSemantics);
            if (IntPtr.Size == 8) Marshal.WriteInt32(buffer, sizeof(int), 0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length - sizeof(char));
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);

            var error = WindowsRenameError.Value ?? (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size)
                ? 0 : Marshal.GetLastPInvokeError());
            if (error is 1 or 50 or 87 or 120)
                throw NativeError("Atomic authority replacement requires FileRenameInfoEx with POSIX semantics on local NTFS", error);
            if (error != 0) throw NativeError("Atomic authority replacement (FileRenameInfoEx)", error);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : @"\\?\" + path;

    internal static void EnsureSupportedFileSystem(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var volume = new StringBuilder(32768);
            var format = new StringBuilder(32);
            if (!GetVolumePathName(path, volume, (uint)volume.Capacity) ||
                !GetVolumeInformation(volume.ToString(), IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    format, (uint)format.Capacity))
                throw new AuthorityFileSystemException("Cannot identify the authority volume.",
                    NativeError("Identify authority volume", Marshal.GetLastPInvokeError()));
            if (GetDriveType(volume.ToString()) != 3 || !string.Equals(format.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new AuthorityFileSystemException("Windows authority storage requires a local NTFS volume.");
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Authority storage requires Windows or Linux.");
        // struct statfs starts with a native long f_type. The remaining Linux fields fit in this buffer.
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (StatFs(path, buffer) != 0)
                throw new AuthorityFileSystemException("Cannot identify the authority filesystem.",
                    NativeError("Identify authority filesystem", Marshal.GetLastPInvokeError()));
            var magic = unchecked((uint)Marshal.ReadIntPtr(buffer).ToInt64());
            // Allow the ext filesystem magic and XFS. This also excludes network mounts implemented through FUSE.
            if (magic is not (0xef53 or 0x58465342))
                throw new AuthorityFileSystemException($"Authority storage requires local ext4 or XFS (statfs magic 0x{magic:X8}).");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsLinux()) FlushLinux(path, wholeFileSystem: false);
        // On Windows FileStream.Flush(true) flushes file data via FlushFileBuffers.
        // There is no ordinary directory-handle fsync equivalent for this publication path.
    }

    // S3 calls this after staging Linux release payload and metadata, before publishing HEAD.
    internal static void SyncFileSystem(string path)
    {
        if (OperatingSystem.IsLinux()) FlushLinux(path, wholeFileSystem: true);
        else throw new PlatformNotSupportedException("Flush individual release files on Windows.");
    }

    private static void FlushLinux(string path, bool wholeFileSystem)
    {
        // Linux ARM and AArch64 use their own O_DIRECTORY value.
        var directoryFlag = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x4000 : 0x10000;
        var descriptor = OpenDirectory(path, directoryFlag);
        if (descriptor < 0) throw NativeError("Open authority directory", Marshal.GetLastPInvokeError());
        try
        {
            int result, error;
            do
            {
                result = wholeFileSystem ? SyncFs(descriptor) : Fsync(descriptor);
                error = Marshal.GetLastPInvokeError();
            } while (result != 0 && error == 4); // EINTR
            if (result != 0) throw NativeError(wholeFileSystem ? "Authority syncfs" : "Authority directory fsync", error);
        }
        finally { Close(descriptor); }
    }

    internal static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) when (OperatingSystem.IsWindows()) { return false; }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return false; }
    }

    private static IOException NativeError(string operation, int error) =>
        new($"{operation} failed (OS error {error}).", new Win32Exception(error));

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int RenameLinux(string source, string destination);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int sourceDirectory, string source, int destinationDirectory, string destination, int flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "syncfs", SetLastError = true)]
    private static extern int SyncFs(int descriptor);
    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int StatFs(string path, IntPtr stat);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, FileShare share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string volume, IntPtr volumeName, uint volumeNameLength, IntPtr serialNumber,
        IntPtr maximumComponentLength, IntPtr flags, StringBuilder format, uint formatLength);
    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string volume);
}
