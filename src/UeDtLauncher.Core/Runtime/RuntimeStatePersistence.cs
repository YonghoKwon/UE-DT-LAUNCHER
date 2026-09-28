using System.Text.Json;
namespace UeDtLauncher;

// Internal AsyncLocal seam: no production command/setting can enable fault injection.
internal static class RuntimeStatePersistence
{
    internal static readonly AsyncLocal<Action<string,string>?> Boundary = new();
    internal static void Write<T>(string path,T value)
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
            Boundary.Value?.Invoke(path,"replace"); File.Move(temporary,path,true);
            Boundary.Value?.Invoke(path,"ack");
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}
