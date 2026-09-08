namespace LanFileSync;

public sealed class FileEntry
{
    public FileEntry(string relPath, long size, long mTimeUtcTicks)
    {
        RelPath = relPath;
        Size = size;
        MTimeUtcTicks = mTimeUtcTicks;
    }

    public string RelPath { get; }
    public long Size { get; }
    public long MTimeUtcTicks { get; }
}