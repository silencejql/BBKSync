using System.IO;

namespace LanFileSync;

public sealed class SyncEngine
{
    public string Root { get; }
    public SyncOptions Options { get; }

    private readonly List<FileEntry> _remote = new();
    private readonly List<string> _needed = new();

    public SyncEngine(string root, SyncOptions options)
    {
        Root = root;
        Options = options;
    }

    public IReadOnlyList<string> NeedList => _needed;

    public void Plan(IEnumerable<FileEntry> remote)
    {
        _remote.Clear();
        _remote.AddRange(remote);

        if (Options.FullReplaceBin)
        {
            string bin = Path.Combine(Root, "Bin");
            if (Directory.Exists(bin))
            {
                SafeHardDelete(bin);
                if (Directory.Exists(bin))
                    throw new IOException("无法删除 Bin 文件夹（被占用）: " + bin);
            }
        }

        _needed.Clear();
        foreach (var f in _remote)
        {
            if (UploadRules.IsSkippedSync(f.RelPath))
                continue;
            if (NeedsUpdate(f))
                _needed.Add(f.RelPath);
        }
    }

    private bool NeedsUpdate(FileEntry remote)
    {
        string local = Path.Combine(Root, remote.RelPath);
        var fi = new FileInfo(local);
        if (!fi.Exists)
            return true;
        if (fi.Length != remote.Size)
            return true;
        long diff = Math.Abs(fi.LastWriteTimeUtc.Ticks - remote.MTimeUtcTicks);
        return diff > Constants.TimeTolerance.Ticks;
    }

    public async Task ReceiveAndApplyAsync(PeerConnection peer, Action<string, long>? onFile, Action<string>? onError, CancellationToken ct)
    {
        long received = 0;
        while (true)
        {
            var frame = await peer.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");

            string op = frame.GetProperty("op").GetString()!;
            if (op == "done")
                break;
            if (op == "err")
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == "skip")
            {
                string sp = frame.GetProperty("p").GetString() ?? "";
                string sm = frame.GetProperty("msg").GetString() ?? "";
                onError?.Invoke($"{sp}  源文件读取失败，已跳过: {sm}");
                continue;
            }
            if (op != "data")
                throw new InvalidOperationException("未知消息: " + op);

            string rel = frame.GetProperty("p").GetString()!;
            long size = frame.GetProperty("s").GetInt64();
            long mtime = frame.GetProperty("t").GetInt64();

            try
            {
                await DownloadAndApplyAsync(peer, rel, size, mtime, ct);
                received += size;
                onFile?.Invoke(rel, size);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                onError?.Invoke($"{rel}: {ex.Message}");
            }
        }
    }

    public async Task CopyNeededFromAsync(string sourceRoot, Action<string, long>? onFile, Action<string>? onError, CancellationToken ct)
    {
        var buf = new byte[Constants.StreamBufferSize];
        foreach (var rel in _needed)
        {
            ct.ThrowIfCancellationRequested();

            long size = 0;
            try { size = new FileInfo(Path.Combine(sourceRoot, rel)).Length; } catch { }

            string target = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                await using (var src = new FileStream(Path.Combine(sourceRoot, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Constants.StreamBufferSize, FileOptions.SequentialScan))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
                {
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }

                MoveIntoPlace(tmp, target);
                try
                {
                    var fi = new FileInfo(Path.Combine(sourceRoot, rel));
                    File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
                }
                catch { }
                onFile?.Invoke(rel, size);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                onError?.Invoke(rel);
            }
        }
    }

    private async Task DownloadAndApplyAsync(PeerConnection peer, string rel, long size, long mtimeTicks, CancellationToken ct)
    {
        string target = Path.Combine(Root, rel);
        string dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
        {
            long remaining = size;
            var buf = new byte[Constants.StreamBufferSize];
            while (remaining > 0)
            {
                int n = (int)Math.Min(buf.LongLength, remaining);
                await peer.ReadRawAsync(buf, n, ct);
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                remaining -= n;
            }
        }

        MoveIntoPlace(tmp, target);
        try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    private void MoveIntoPlace(string tmp, string target)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (IOException) when (Options.KillFreeFormFirst && attempt < Constants.MaxRetryCount)
            {
                attempt++;
                FreeFormKiller.KillAll();
                Thread.Sleep(Constants.RetryDelayMs * attempt);
            }
        }
    }

    private void SafeHardDelete(string path)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (Options.KillFreeFormFirst && attempt < Constants.MaxRetryCount)
            {
                attempt++;
                FreeFormKiller.KillAll();
                Thread.Sleep(Constants.RetryDelayMs * attempt);
            }
        }
    }
}