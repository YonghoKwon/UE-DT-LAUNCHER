using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace UeDtLauncher.Gui;

// GUI activation is local to the user, desktop session and launcher edition.
// The pipe conveys only an activation signal and never carries launcher arguments.
internal sealed class LauncherGuiInstance(string pipeName, SingleInstanceLock lease) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _listener = Task.CompletedTask;
    internal Action? Activate { get; set; }

    internal static LauncherGuiInstance? AcquireOrActivate(string preferencesPath, LauncherEdition edition)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(preferencesPath))!;
        using var process = Process.GetCurrentProcess();
        var session = OperatingSystem.IsWindows()
            ? process.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID") ??
                Environment.GetEnvironmentVariable("DISPLAY") ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var scope = OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\n{edition}\n{session}")));
        var pipe = "ue-dt-gui-" + hash[..32];
        var lockPath = Path.Combine(directory, pipe + ".lock");
        var wait = Stopwatch.StartNew();
        do
        {
            if (SingleInstanceLock.TryAcquire(lockPath, out var acquired))
                return new(pipe, acquired!);
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.In, PipeOptions.CurrentUserOnly);
            try
            {
                client.Connect(200);
                return null;
            }
            catch (TimeoutException) { }
            catch (IOException) { }
        }
        while (wait.Elapsed < TimeSpan.FromSeconds(5));
        throw new IOException("기존 런처 창을 열 수 없습니다.");
    }

    internal void StartListening()
    {
        var token = _stop.Token;
        _listener = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                if (!token.IsCancellationRequested) Activate?.Invoke();
            }
        }, token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            _stop.Dispose();
            lease.Dispose();
        }
    }
}
