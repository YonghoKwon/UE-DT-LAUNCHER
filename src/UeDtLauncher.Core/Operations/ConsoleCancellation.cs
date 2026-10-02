namespace UeDtLauncher;

public sealed class ConsoleCancellation : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    public CancellationToken Token => cancellation.Token;
    public ConsoleCancellation() => Console.CancelKeyPress += OnCancel;
    private void OnCancel(object? sender, ConsoleCancelEventArgs args) { args.Cancel = true; cancellation.Cancel(); }
    public void Dispose() { Console.CancelKeyPress -= OnCancel; cancellation.Dispose(); }
}
