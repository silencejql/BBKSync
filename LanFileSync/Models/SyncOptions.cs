namespace LanFileSync;

public sealed class SyncOptions
{
    public bool FullReplaceBin { get; init; }
    public bool KillFreeFormFirst { get; init; } = true;
}