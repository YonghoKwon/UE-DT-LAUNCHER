using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher;

public sealed record RuntimeIdentity(int Pid, string CreationId, string Executable, string Owner, string Session, bool Administrator);

public static class RuntimeIdentities
{
    public static RuntimeIdentity Current() => Read(Environment.ProcessId);
    public static RuntimeIdentity Read(int pid)
    {
        if (pid <= 0) throw new InvalidDataException("Invalid process identity.");
        if (OperatingSystem.IsWindows()) return ReadWindows(pid);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var root = "/proc/" + pid;
        var stat = File.ReadAllText(root + "/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20) throw new InvalidDataException("Process identity unavailable.");
        var uid = File.ReadLines(root + "/status").Single(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[2];
        var image = File.ResolveLinkTarget(root + "/exe", true)?.FullName ?? throw new IOException("Process image unavailable.");
        var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        return new(pid, boot + ":" + fields[19], Path.GetFullPath(image), uid, fields[3], uid == "0");
    }
    public static bool StillMatches(RuntimeIdentity identity)
    {
        try { return Read(identity.Pid) == identity; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static RuntimeIdentity ReadWindows(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new UnauthorizedAccessException("Cannot inspect process identity.");
        if (!GetProcessTimes(process, out var created, out _, out _, out _)) throw new IOException("Process creation identity unavailable.");
        var image = new StringBuilder(32768); var size = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref size) || !ProcessIdToSessionId(pid, out var session)) throw new IOException("Process image/session unavailable.");
        if (!OpenProcessToken(process, 8, out var token)) throw new UnauthorizedAccessException("Cannot inspect process owner.");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return new(pid, created.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(image.ToString()), identity.User!.Value, session.ToString(), IsEnabledAdministrator(token));
    }
    [SupportedOSPlatform("windows")]
    private static bool IsEnabledAdministrator(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, 2, IntPtr.Zero, 0, out var length);
        if (length is <= 0 or > 65536) throw new UnauthorizedAccessException("Token groups unavailable.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, length, out _)) throw new UnauthorizedAccessException("Token groups unavailable.");
            var count = Marshal.ReadInt32(buffer);
            var offset = IntPtr.Size == 8 ? 8 : 4; var stride = IntPtr.Size == 8 ? 16 : 8;
            if (count < 0 || offset + count * stride > length) throw new InvalidDataException("Invalid token groups.");
            for (var i = 0; i < count; i++)
            {
                var entry = IntPtr.Add(buffer, offset + i * stride);
                var flags = Marshal.ReadInt32(entry, IntPtr.Size);
                if ((flags & 4) != 0 && (flags & 16) == 0 && new SecurityIdentifier(Marshal.ReadIntPtr(entry)).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeFileHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeFileHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int info, IntPtr buffer, int length, out int returned);
}
