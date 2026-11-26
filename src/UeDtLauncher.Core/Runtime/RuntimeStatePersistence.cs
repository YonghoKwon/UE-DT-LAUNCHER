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

    internal static JsonDocument? ReadSnapshot(string path)
    {
        FileStream file;
        try
        {
            file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        using (file)
        {
            if (file.Length > 64 * 1024) throw new InvalidDataException("Runtime record exceeds limit.");
            using var reader = new StreamReader(file);
            return JsonDocument.Parse(reader.ReadToEnd());
        }
    }

    // Other callers (operation and cache records) retain their existing persistence contract.
    internal static void Write<T>(string path, T value) => Write(path, value, runtimeSnapshot: false);
    internal static void WriteSnapshot<T>(string path, T value) => Write(path, value, runtimeSnapshot: true);

    private static void Write<T>(string path, T value, bool runtimeSnapshot)
    {
        path=Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            if (!runtimeSnapshot || !OperatingSystem.IsWindows() || !TryReplaceWindows(temporary, path))
                File.Move(temporary, path, overwrite: true);
            Boundary.Value?.Invoke(path,"ack");
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    private const int FileRenameInfoEx = 22;
    private const int ReplaceIfExistsAndPosixSemantics = 3;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;

    private static bool TryReplaceWindows(string source, string destination)
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

        if (SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)buffer.Length)) return true;
        var error = Marshal.GetLastPInvokeError();
        // Preserve the earlier atomic move on hosts/file systems without the extended rename capability.
        if (error is 1 or 50 or 87 or 120) return false;
        throw RenameError(error);
    }

    private static IOException RenameError(int error) =>
        new("Runtime snapshot could not be atomically replaced.", new Win32Exception(error));

    private static string ExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] buffer, uint size);
}
