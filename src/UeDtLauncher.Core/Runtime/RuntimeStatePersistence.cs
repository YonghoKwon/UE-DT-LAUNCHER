using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace UeDtLauncher;

// Internal AsyncLocal seam: no production command/setting can enable fault injection.
internal static class RuntimeStatePersistence
{
    internal static readonly AsyncLocal<Action<string,string>?> Boundary = new();
    internal static readonly AsyncLocal<int?> WindowsRenameError = new();

    internal static FileStream? OpenSnapshot(string path)
    {
        try
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static JsonDocument? ReadSnapshot(string path)
    {
        using var file = OpenSnapshot(path);
        if (file is null) return null;
        if (file.Length > 64 * 1024) throw new InvalidDataException("Runtime record exceeds limit.");
        using var reader = new StreamReader(file, leaveOpen: true);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    // Other callers (operation and cache records) retain their existing persistence contract.
    internal static void Write<T>(string path, T value) => Write(path, value, runtimeSnapshot: false);
    internal static void WriteSnapshot<T>(string path, T value) => Write(path, value, runtimeSnapshot: true);

    private static void Write<T>(string path, T value, bool runtimeSnapshot)
    {
        path=Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Boundary.Value?.Invoke(path,"write");
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file,value,JsonFiles.Options);
                Boundary.Value?.Invoke(path,"flush"); file.Flush(true);
            }
            Boundary.Value?.Invoke(path,"replace");
            if (runtimeSnapshot && OperatingSystem.IsWindows())
                ReplaceWindows(temporary, path);
            else
                File.Move(temporary, path, overwrite: true);
            if (runtimeSnapshot && OperatingSystem.IsLinux())
            {
                Boundary.Value?.Invoke(path, "directory-flush");
                FlushLinuxDirectory(directory);
            }
            Boundary.Value?.Invoke(path,"ack");
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    private const int FileRenameInfoEx = 22;
    private const int ReplaceIfExistsAndPosixSemantics = 3;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;

    private static void ReplaceWindows(string source, string destination)
    {
        using var handle = CreateFile(ExtendedPath(source), DeleteAccess, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw RenameError(Marshal.GetLastPInvokeError());

        // FILE_RENAME_INFO contains flags, an aligned null directory handle, then the byte count and UTF-16 name.
        var name = Encoding.Unicode.GetBytes(ExtendedPath(destination) + "\0");
        int lengthOffset = 2 * IntPtr.Size;
        var buffer = new byte[lengthOffset + sizeof(int) + name.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, ReplaceIfExistsAndPosixSemantics);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(lengthOffset), name.Length - sizeof(char));
        name.CopyTo(buffer, lengthOffset + sizeof(int));

        var error = WindowsRenameError.Value ?? (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)buffer.Length)
            ? 0 : Marshal.GetLastPInvokeError());
        if (error is 1 or 50 or 87 or 120)
            throw new IOException("Atomic runtime snapshot replacement requires FileRenameInfoEx with POSIX semantics.", new Win32Exception(error));
        if (error != 0) throw RenameError(error);
    }

    private static IOException RenameError(int error) =>
        new("Runtime snapshot could not be atomically replaced.", new Win32Exception(error));

    private static string ExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static void FlushLinuxDirectory(string path)
    {
        // Linux ARM and AArch64 use a different O_DIRECTORY value.
        var directoryFlag = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x4000 : 0x10000;
        const int closeOnExec = 0x80000;
        const int interrupted = 4;
        var descriptor = OpenDirectory(path, directoryFlag | closeOnExec);
        if (descriptor < 0) throw new IOException("Could not open runtime snapshot directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            int result, error;
            do
            {
                result = Fsync(descriptor);
                error = Marshal.GetLastPInvokeError();
            } while (result != 0 && error == interrupted);
            if (result != 0) throw new IOException("Runtime snapshot directory fsync failed.", new Win32Exception(error));
        }
        finally { Close(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] buffer, uint size);
}
