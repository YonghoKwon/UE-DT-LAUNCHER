using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UeDtLauncher;

// Test-only executable. No reference from any product/publish/installer project.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
if (args[0] == "guard")
{
    var savedOut = Console.Out; var savedError = Console.Error;
    var output = Native.GetStdHandle(-11); var error = Native.GetStdHandle(-12);
    var handle = args[1] == "null" ? IntPtr.Zero : new IntPtr(-1);
    if (args[1] == "closed")
    {
        handle = Native.CreateFile("NUL", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Native.CloseHandle(handle);
    }
    object result;
    try
    {
        if (args[1] != "pipe") { Native.SetStdHandle(-11, handle); Native.SetStdHandle(-12, handle); }
        CliErrors.ConfigureRedirectedConsole();
        result = new { mode = args[1], outputWriterPreserved = ReferenceEquals(savedOut, Console.Out), errorWriterPreserved = ReferenceEquals(savedError, Console.Error) };
        if (args[1] == "pipe") { Console.WriteLine("한글 출력"); Console.Error.WriteLine("한글 오류"); }
    }
    finally
    {
        Native.SetStdHandle(-11, output); Native.SetStdHandle(-12, error);
        Console.SetOut(savedOut); Console.SetError(savedError);
    }
    File.WriteAllText(args[2], JsonSerializer.Serialize(result), new UTF8Encoding(false));
}
else if (args[0] == "console")
{
    var result = Native.Capture(Path.GetFullPath(args[1]), args[2], args.Skip(4).ToArray());
    File.WriteAllText(args[3], JsonSerializer.Serialize(result), new UTF8Encoding(false));
}
else throw new ArgumentException("Use guard or console.");

internal static class Native
{
    internal static object Capture(string target, string shell, string[] arguments)
    {
        // This child owns a private console. Its screen buffer is read directly, not redirected from the target.
        FreeConsole();
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
        var window = GetConsoleWindow();
        if (window != IntPtr.Zero) ShowWindow(window, 0);
        var buffer = CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (buffer == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var input = CreateFile("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        SetStdHandle(-10, input); SetStdHandle(-11, buffer); SetStdHandle(-12, buffer);
        var job = CreateJobObject(IntPtr.Zero, null);
        ProcessInformation process = default;
        try
        {
            if (!SetConsoleScreenBufferSize(buffer, new(220, 4000))) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // Kill only the children assigned to this owned test job on failure/close.
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            string executable, command;
            if (shell == "control")
            {
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                command = Quote(executable) + " /d /c echo CONSOLE_POSITIVE_CONTROL";
            }
            else if (shell is "powershell" or "powershell-file")
            {
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe");
                // A real interactive shell stays alive after launching WinExe; do not end -Command before its child can attach.
                var values = shell == "powershell-file" ? arguments.Skip(1) : arguments;
                var script = "& '" + target.Replace("'", "''") + "' " + string.Join(" ", values.Select(a => "'" + a.Replace("'", "''") + "'"));
                if (shell == "powershell-file")
                    script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " + script + " 2>&1 | Out-File -Encoding utf8 '" + arguments[0].Replace("'", "''") + "'";
                script += "; Start-Sleep -Seconds 2";
                command = Quote(executable) + " -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            }
            else if (shell is "cmd" or "cmd-file")
            {
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                var values = shell == "cmd-file" ? arguments.Skip(1) : arguments;
                var redirect = shell == "cmd-file" ? " 1>" + Quote(arguments[0] + ".out") + " 2>" + Quote(arguments[0] + ".err") : "";
                command = Quote(executable) + " /d /s /c \"" + Quote(target) + " " + string.Join(" ", values.Select(Quote)) + redirect + "\"";
            }
            else
            {
                executable = target;
                command = Quote(target) + " " + string.Join(" ", arguments.Select(Quote));
            }
            // No STARTF_USESTDHANDLES and no handle inheritance: GUI-subsystem children must attach themselves.
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
            if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 4, IntPtr.Zero, Path.GetDirectoryName(target), ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, process.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ResumeThread(process.Thread);
            var started = Environment.TickCount64;
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out Accounting state, Marshal.SizeOf<Accounting>(), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (state.ActiveProcesses == 0) break;
                if (Environment.TickCount64 - started > 30000) throw new TimeoutException("Owned console test process did not finish.");
                Thread.Sleep(20);
            }
            GetExitCodeProcess(process.Process, out var exit);
            if (!GetConsoleScreenBufferInfo(buffer, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var characters = new char[info.Size.X * (info.Cursor.Y + 1)];
            if (!ReadConsoleOutputCharacter(buffer, characters, characters.Length, new(0, 0), out var read)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var raw = new string(characters, 0, read); var lines = new List<string>();
            for (var offset = 0; offset < raw.Length; offset += info.Size.X)
                lines.Add(raw.Substring(offset, Math.Min(info.Size.X, raw.Length - offset)).TrimEnd(' ', '\0'));
            return new { shell, exitCode = exit, text = string.Join("\n", lines).Trim(), standardHandlesInherited = false, screenBufferCapture = true };
        }
        finally
        {
            if (job != IntPtr.Zero)
            {
                TerminateJobObject(job, 99);
                var deadline = Environment.TickCount64 + 5000;
                while (QueryInformationJobObject(job, 1, out Accounting state, Marshal.SizeOf<Accounting>(), IntPtr.Zero) && state.ActiveProcesses > 0 && Environment.TickCount64 < deadline)
                    Thread.Sleep(10);
            }
            // Assignment itself can fail (e.g. host job policy). The suspended child is still owned by this exact handle.
            if (process.Process != IntPtr.Zero && GetExitCodeProcess(process.Process, out var code) && code == 259)
            { TerminateProcess(process.Process, 99); WaitForSingleObject(process.Process, 5000); }
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (job != IntPtr.Zero) CloseHandle(job);
            CloseHandle(input); CloseHandle(buffer); FreeConsole();
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    [StructLayout(LayoutKind.Sequential)] internal struct Coord(short x, short y) { public short X = x; public short Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public short Attributes; public Rectangle Window; public Coord Maximum; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long UserTime, JobTime; public uint LimitFlags; public UIntPtr Minimum, Maximum; public uint ActiveLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint PageFaults, TotalProcesses, ActiveProcesses, Terminated; }
    [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetStdHandle(int id, IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)] internal static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleScreenBufferSize(IntPtr buffer, Coord size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr buffer, out BufferInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputCharacterW", SetLastError = true)] private static extern bool ReadConsoleOutputCharacter(IntPtr buffer, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] text, int length, Coord start, out int read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting info, int size, IntPtr returned);
}
