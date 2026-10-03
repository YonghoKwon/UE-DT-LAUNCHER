using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher.Distribution;

internal static class MaintenanceStorage
{
    internal static void PrivateDirectory(string path)
    {
        var existed=Directory.Exists(path);Directory.CreateDirectory(path);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked maintenance directory.");
        if(OperatingSystem.IsWindows())
        {
            var directory=new DirectoryInfo(path);var me=WindowsIdentity.GetCurrent().User!;
            if(!existed)
            {
                var acl=new DirectorySecurity();acl.SetOwner(me);acl.SetAccessRuleProtection(true,false);
                foreach(var sid in new[]{me,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                    acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                directory.SetAccessControl(acl);
            }
            var current=directory.GetAccessControl();
            if(!current.AreAccessRulesProtected || current.GetOwner(typeof(SecurityIdentifier))?.Value!=me.Value)throw new UnauthorizedAccessException("Unsafe maintenance owner/inheritance.");
            foreach(FileSystemAccessRule rule in current.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if(rule.AccessControlType!=AccessControlType.Allow || ((SecurityIdentifier)rule.IdentityReference).Value is var id && id!=me.Value && id!="S-1-5-18" && id!="S-1-5-32-544")
                    throw new UnauthorizedAccessException("Unsafe maintenance ACL.");
        }
        else if(OperatingSystem.IsLinux())
        {
            if(!existed)File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            if(RuntimeIdentities.DirectoryOwner(path)!=RuntimeIdentities.Current().Owner || (File.GetUnixFileMode(path)&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead|UnixFileMode.GroupExecute|UnixFileMode.OtherExecute))!=0)
                throw new UnauthorizedAccessException("Unsafe maintenance mode/owner.");
        }
        else throw new PlatformNotSupportedException();
    }
    internal static string DirectoryIdentity(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked directory identity.");
        if(OperatingSystem.IsWindows())
        {
            using var handle=CreateFile(path,0x80,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(handle.IsInvalid || !GetFileInformationByHandle(handle,out var info))throw new IOException("Directory identity unavailable.");
            return "win:"+info.Volume+":"+info.IndexHigh+":"+info.IndexLow;
        }
        if(!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw new PlatformNotSupportedException();
        var fd=open(path,0x200000|0x20000|0x80000);if(fd<0)throw new IOException("Directory identity unavailable.");
        var stat=Marshal.AllocHGlobal(256);
        try{if(fstat(fd,stat)!=0)throw new IOException("Directory identity unavailable.");return "linux:"+Marshal.ReadInt64(stat,0)+":"+Marshal.ReadInt64(stat,8);}
        finally{Marshal.FreeHGlobal(stat);close(fd);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInfoNative{public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInfoNative info);
    [DllImport("libc",SetLastError=true)]private static extern int open(string path,int flags);
    [DllImport("libc",SetLastError=true)]private static extern int fstat(int fd,IntPtr buffer);
    [DllImport("libc")]private static extern int close(int fd);
}
