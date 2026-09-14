using System.IO;

namespace LanFileSync;

public sealed class BackupSyncEngine
{
    public string Dest { get; }
    public BackupOptions Options { get; }

    private readonly List<FileEntry> _remote = new();
    private readonly List<string> _needed = new();

    public BackupSyncEngine(string dest, BackupOptions options)
    {
        Dest = dest;
        Options = options;
    }

    public IReadOnlyList<string> NeedList => _needed;

    public void Plan(IEnumerable<FileEntry> remote)
    {
        _remote.Clear();
        _remote.AddRange(remote);

        DateTime cut = DateTime.UtcNow.AddDays(-Options.LogRetentionDays);

        _needed.Clear();
        foreach (var f in _remote)
        {
            if (UploadRules.IsTmpFile(f.RelPath))
                continue;
            if (Options.IsIgnored(f.RelPath))
                continue;
            if (Options.ApplyLogRule
                && UploadRules.HasSegment(f.RelPath, "Log")
                && f.MTimeUtcTicks < cut.Ticks)
                continue;
            if (NeedsFetch(f))
                _needed.Add(f.RelPath);
        }
    }

    private bool NeedsFetch(FileEntry remote)
    {
        string local = Path.Combine(Dest, remote.RelPath);
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

            await WriteOneAsync(peer, rel, size, mtime, onError, ct);
            onFile?.Invoke(rel, size);
        }
    }

    private async Task WriteOneAsync(PeerConnection peer, string rel, long size, long mtimeTicks, Action<string>? onError, CancellationToken ct)
    {
        string target = Path.Combine(Dest, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        long consumed = 0;
        byte[] buf = new byte[Constants.StreamBufferSize];

        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
            {
                while (consumed < size)
                {
                    int n = (int)Math.Min(buf.LongLength, size - consumed);
                    await peer.ReadRawAsync(buf, n, ct);
                    consumed += n;
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
        }
        catch (IOException)
        {
            while (consumed < size)
            {
                int n = (int)Math.Min(buf.LongLength, size - consumed);
                await peer.ReadRawAsync(buf, n, ct);
                consumed += n;
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            onError?.Invoke(rel + "  写入失败，已跳过");
            return;
        }

        try
        {
            File.Move(tmp, target, overwrite: true);
        }
        catch (IOException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            onError?.Invoke(rel + "  无法覆盖(文件被占用等)，已跳过");
            return;
        }

        try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
    }
}