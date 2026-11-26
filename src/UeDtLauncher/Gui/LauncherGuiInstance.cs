using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace UeDtLauncher.Gui;

// GUI activation is local to the user, desktop session and launcher edition.
// The pipe accepts a connection as an activation request and returns a visibility acknowledgement.
internal sealed class LauncherGuiInstance : IDisposable
{
    private readonly SingleInstanceLock _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Func<CancellationToken, Task<bool>>> _activation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _listener;

    private LauncherGuiInstance(string pipeName, SingleInstanceLock lease)
    {
        _lease = lease;
        // Publish a receiver before Avalonia starts loading fonts, assets and the window.
        var server = CreateReceiver(pipeName);
        _listener = ListenAsync(pipeName, server, _stop.Token);
    }

    internal static LauncherGuiInstance? AcquireOrActivate(string preferencesPath, LauncherEdition edition) =>
        AcquireOrActivateAsync(preferencesPath, edition).GetAwaiter().GetResult();

    private static async Task<LauncherGuiInstance?> AcquireOrActivateAsync(string preferencesPath, LauncherEdition edition)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var acknowledgement = new byte[1];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (SingleInstanceLock.TryAcquire(lockPath, out var lease))
                {
                    try { return new(pipe, lease!); }
                    catch { lease!.Dispose(); throw; }
                }
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
                    PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                try
                {
                    await client.ConnectAsync(250, token).ConfigureAwait(false);
                    if (await client.ReadAsync(acknowledgement, token).ConfigureAwait(false) == 1 &&
                        acknowledgement[0] == 1) return null;
                }
                catch (Exception error) when (error is TimeoutException or IOException) { }
                // A closing owner may have dropped the pipe before releasing the lease.
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        throw new IOException("기존 런처 창을 열 수 없습니다.");
    }

    internal void SetActivationHandler(Func<CancellationToken, Task<bool>> activate) => _activation.SetResult(activate);

    private static NamedPipeServerStream CreateReceiver(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(string pipeName, NamedPipeServerStream receiver, CancellationToken token)
    {
        var displayed = new byte[] { 1 };
        var receipt = new byte[1];
        while (true)
        {
            using (receiver)
            {
                try
                {
                    await receiver.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var activate = await _activation.Task.WaitAsync(token).ConfigureAwait(false);
                    if (await activate(token).ConfigureAwait(false) && !token.IsCancellationRequested)
                    {
                        await receiver.WriteAsync(displayed, token).ConfigureAwait(false);
                        // Do not tear down a Windows pipe before the contender can read the response.
                        // The contender closes it immediately after consuming the acknowledgement.
                        using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
                        drain.CancelAfter(TimeSpan.FromSeconds(2));
                        try { await receiver.ReadAsync(receipt, drain.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    }
                }
                catch (IOException) { /* The contender exited before receiving its acknowledgement. */ }
            }
            token.ThrowIfCancellationRequested();
            receiver = CreateReceiver(pipeName);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        finally
        {
            _stop.Dispose();
            _lease.Dispose();
        }
    }
}
