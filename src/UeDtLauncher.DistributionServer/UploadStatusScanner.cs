namespace UeDtLauncher.Distribution;

internal static class UploadStatusScanner
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(IntakeStore store, Action<Exception> logFailure, CancellationToken token)
    {
        var projection = new UploadStatusProjection(store, logFailure);
        try
        {
            await projection.RefreshAsync(token);
            while (!token.IsCancellationRequested)
            {
                // A scan can do synchronous work before its first yield. Give the status
                // timer its own continuation so it can observe the live job during that work.
                var scan = Task.Run(() => store.ScanAsync(token), token);
                using (var timer = new PeriodicTimer(Interval))
                {
                    try
                    {
                        while (!scan.IsCompleted)
                        {
                            var tick = timer.WaitForNextTickAsync(token).AsTask();
                            if (await Task.WhenAny(scan, tick) == scan || !await tick) break;
                            await projection.RefreshAsync(token);
                        }
                    }
                    finally { await scan; }
                }
                await projection.RefreshAsync(token);
                await Task.Delay(Interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
