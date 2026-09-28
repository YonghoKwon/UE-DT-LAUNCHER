using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UeDtLauncher;

public sealed record RuntimeHostRequest(string Executable, string WorkingDirectory, string[] Arguments);
public sealed record RuntimeFamilyResult(int RootPid, int RootExitCode, int ReapedProcesses, string Mechanism);

/// <summary>Dedicated runtime-host only. Never mix this Linux wait loop with managed child-process APIs.</summary>
internal static class NativeProcessFamily
{
    internal static void PrepareHost()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Linux runtime supervision requires x64.");
        if (prctl(36, 1, 0, 0, 0) != 0) ThrowUnix("Enable child subreaper");
        int enabled = 0;
        if (prctl_get(37, ref enabled, 0, 0, 0) != 0 || enabled != 1) throw new PlatformNotSupportedException("Child subreaper capability is unavailable.");
        if (getsid(0) != getpid() && setsid() < 0) ThrowUnix("Detach runtime session");
    }
    internal static RuntimeFamilyResult Run(RuntimeHostRequest request, Action<int> started)
    {
        if (!Path.IsPathFullyQualified(request.Executable) || !File.Exists(request.Executable) || !Path.IsPathFullyQualified(request.WorkingDirectory) ||
            !Directory.Exists(request.WorkingDirectory) || request.Arguments is null || request.Arguments.Length > 256 || request.Arguments.Any(a => a is null || a.Contains('\0')))
            throw new InvalidDataException("Invalid runtime host launch specification.");
        return OperatingSystem.IsWindows() ? RunWindows(request, started) : OperatingSystem.IsLinux() ? RunLinux(request, started) : throw new PlatformNotSupportedException();
    }

    internal static string QuoteWindows(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"')) return argument;
        var output = new StringBuilder("\""); var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { output.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            output.Append('\\', slashes).Append(c); slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static RuntimeFamilyResult RunWindows(RuntimeHostRequest request, Action<int> started)
    {
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) ThrowWin32("Create job");
        // Default job limits prohibit breakaway and do NOT kill children on handle close.
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        using var nul = CreateFile("NUL", 0xc0000000, 3, ref attributes, 3, 0, IntPtr.Zero);
        if (nul.IsInvalid) ThrowWin32("Open null streams");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100, Input = nul.DangerousGetHandle(), Output = nul.DangerousGetHandle(), Error = nul.DangerousGetHandle() };
        var command = string.Join(' ', new[] { request.Executable }.Concat(request.Arguments).Select(QuoteWindows));
        if (command.Length >= 32767) throw new InvalidDataException("Runtime command line exceeds platform limit.");
        if (!CreateProcess(request.Executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08000004, IntPtr.Zero, request.WorkingDirectory, ref startup, out var info))
            ThrowWin32("Create suspended payload");
        using var process = new SafeFileHandle(info.Process, true);
        using var thread = new SafeFileHandle(info.Thread, true);
        var resumed = false;
        try
        {
            if (!AssignProcessToJobObject(job, process)) ThrowWin32("Assign payload to job");
            if (ResumeThread(thread) == uint.MaxValue) ThrowWin32("Resume payload");
            resumed = true;
            started(checked((int)info.ProcessId));
            JobAccounting accounting;
            do
            {
                if (!QueryInformationJobObject(job, 1, out accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) ThrowWin32("Query job lifetime");
                if (accounting.ActiveProcesses != 0) Thread.Sleep(50);
            } while (accounting.ActiveProcesses != 0);
            if (!GetExitCodeProcess(process, out var exit)) ThrowWin32("Read payload exit");
            return new(checked((int)info.ProcessId), unchecked((int)exit), checked((int)accounting.TotalProcesses), "windows-job");
        }
        catch
        {
            // This handle belongs only to the newly created, never-resumed child. Running payloads
            // are NEVER terminated here, nor on job disposal/host crash.
            if (!resumed) { _ = TerminateProcess(process, 1); _ = WaitForSingleObject(process, 5000); }
            throw;
        }
    }

    private static RuntimeFamilyResult RunLinux(RuntimeHostRequest request, Action<int> started)
    {
        PrepareHost();
        var previous = Directory.GetCurrentDirectory();
        using var argv = new NativeStrings(new[] { request.Executable }.Concat(request.Arguments));
        using var environment = new NativeStrings(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => e.Key + "=" + e.Value));
        var saved = new[] { dup(0), dup(1), dup(2) }; var nul = open("/dev/null", 2);
        var pid = 0;
        try
        {
            if (nul < 0 || saved.Any(fd => fd < 0)) ThrowUnix("Prepare payload streams");
            Directory.SetCurrentDirectory(request.WorkingDirectory);
            for (var i = 0; i < 3; i++) if (dup2(nul, i) < 0) ThrowUnix("Isolate payload streams");
            // Null attributes/actions use POSIX defaults; no guessed opaque libc structure sizes.
            var error = posix_spawn(out pid, request.Executable, IntPtr.Zero, IntPtr.Zero, argv.Pointer, environment.Pointer);
            if (error != 0) throw new IOException("Native payload spawn failed.", new Win32Exception(error));
        }
        finally
        {
            for (var i = 0; i < 3; i++) if (saved[i] >= 0) { _ = dup2(saved[i], i); _ = close(saved[i]); }
            if (nul >= 0) _ = close(nul);
            Directory.SetCurrentDirectory(previous);
        }
        started(pid);
        var reaped = 0; int? rootExit = null;
        while (true)
        {
            var child = waitpid(-1, out var status, 0);
            if (child > 0)
            {
                reaped++;
                if (child == pid) rootExit = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                continue;
            }
            var error = Marshal.GetLastPInvokeError();
            if (child < 0 && error == 4) continue; // EINTR
            if (child < 0 && error == 10 && rootExit.HasValue) return new(pid, rootExit.Value, reaped, "linux-subreaper");
            throw new IOException("Native descendant lifetime is indeterminate.", new Win32Exception(error));
        }
    }

    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] strings;
        public IntPtr Pointer { get; }
        public NativeStrings(IEnumerable<string> values)
        {
            strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(Pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
        }
        public void Dispose() { foreach (var p in strings) Marshal.FreeCoTaskMem(p); Marshal.FreeHGlobal(Pointer); }
    }
    private static void ThrowWin32(string operation) => throw new IOException(operation + " failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
    private static void ThrowUnix(string operation) => throw new PlatformNotSupportedException(operation + " failed (" + Marshal.GetLastPInvokeError() + ").");
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, Terminated; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int info, out JobAccounting accounting, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("libc", SetLastError=true)] private static extern int prctl(int option, long value, long a, long b, long c);
    [DllImport("libc", EntryPoint="prctl", SetLastError=true)] private static extern int prctl_get(int option, ref int value, long a, long b, long c);
    [DllImport("libc")] private static extern int getpid();
    [DllImport("libc", SetLastError=true)] private static extern int getsid(int pid);
    [DllImport("libc", SetLastError=true)] private static extern int setsid();
    [DllImport("libc", SetLastError=true)] private static extern int open(string file, int flags);
    [DllImport("libc", SetLastError=true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError=true)] private static extern int dup2(int oldFd, int newFd);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc", CharSet=CharSet.Ansi)] private static extern int posix_spawn(out int pid, string path, IntPtr actions, IntPtr attributes, IntPtr arguments, IntPtr environment);
    [DllImport("libc", SetLastError=true)] private static extern int waitpid(int pid, out int status, int options);
}
