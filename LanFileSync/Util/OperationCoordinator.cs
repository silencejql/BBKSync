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

    public static string FormatSize(long bytes)
    {
        const long K = 1024, M = 1024 * K, G = 1024 * M;
        return bytes >= G ? $"{bytes / (double)G:F2} GB"
            : bytes >= M ? $"{bytes / (double)M:F2} MB"
            : bytes >= K ? $"{bytes / (double)K:F1} KB"
            : $"{bytes} B";
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
