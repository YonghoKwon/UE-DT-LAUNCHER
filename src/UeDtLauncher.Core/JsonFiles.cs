using System.Text.Json;
using System.Runtime.InteropServices;

namespace UeDtLauncher;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
        return value ?? throw new InvalidOperationException($"Failed to parse JSON file: {path}");
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
        =>await WriteAsync(path,value,true,cancellationToken);

    public static async Task WriteAsync<T>(string path,T value,bool overwrite,CancellationToken cancellationToken=default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fileName = Path.GetFileName(fullPath);
        var tempPath = Path.Combine(directory!, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if(!overwrite && OperatingSystem.IsLinux())
            {
                // File.Move(false) can check existence then rename on Unix; RENAME_NOREPLACE is one atomic decision.
                if(RenameNoReplace(-100,tempPath,-100,fullPath,1)!=0)
                {
                    var error=Marshal.GetLastPInvokeError();
                    // WSL1/filesystems without renameat2: link exclusively publishes the freshly flushed temp,
                    // then finally removes its temporary name. It never links installed payloads or existing data.
                    if(error is not (22 or 38 or 95) || LinkNewFile(tempPath,fullPath)!=0)
                        throw new IOException("Cannot atomically create configuration without replacing an existing file.",new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }
            else File.Move(tempPath, fullPath, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // A leftover temp file is ignored and never read as live state.
            }
        }
    }
    [DllImport("libc",EntryPoint="renameat2",SetLastError=true)]
    private static extern int RenameNoReplace(int sourceDirectory,[MarshalAs(UnmanagedType.LPUTF8Str)] string source,int targetDirectory,[MarshalAs(UnmanagedType.LPUTF8Str)] string target,uint flags);
    [DllImport("libc",EntryPoint="link",SetLastError=true)]
    private static extern int LinkNewFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source,[MarshalAs(UnmanagedType.LPUTF8Str)] string target);
}
