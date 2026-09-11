namespace LanFileSync;

public sealed class OperationCoordinator : IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _busy;

    public bool Busy => _busy;
    public CancellationToken Token => _cts!.Token;

    public void StartBusy()
    {
        _cts = new CancellationTokenSource();
        _busy = true;
    }

    public void EndBusy()
    {
        _cts?.Dispose();
        _cts = null;
        _busy = false;
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
