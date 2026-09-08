using System.IO;

namespace LanFileSync;

public static class SourceSide
{
    public static async Task SendManifestAsync(PeerConnection peer, string root, CancellationToken ct)
    {
        foreach (var f in FileLister.ListFiles(root))
        {
            await peer.SendJsonAsync(new { op = "f", p = f.RelPath, s = f.Size, t = f.MTimeUtcTicks }, ct);
        }
        await peer.SendJsonAsync(new { op = "mend" }, ct);
    }

    public static async Task SendRequestedFilesAsync(
        PeerConnection peer,
        string root,
        IReadOnlyList<string> paths,
        Action<string, long>? onFile,
        CancellationToken ct)
    {
        var buf = new byte[128 * 1024];

        foreach (var path in paths)
        {
            string file = Path.Combine(root, path);

            FileStream? fs = null;
            try
            {
                fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                try
                {
                    await peer.SendJsonAsync(new { op = "skip", p = path, msg = ex.Message }, ct);
                }
                catch { }
                continue;
            }

            using (fs)
            {
                long size = fs.Length;
                var fi = new FileInfo(file);

                await peer.SendJsonAsync(new { op = "data", p = path, s = size, t = fi.LastWriteTimeUtc.Ticks }, ct);

                long remaining = size;
                while (remaining > 0)
                {
                    int read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (read <= 0)
                        throw new EndOfStreamException("读取源文件失败: " + path);
                    await peer.SendRawAsync(buf, read, ct);
                    remaining -= read;
                }

                onFile?.Invoke(path, size);
            }
        }

        await peer.SendJsonAsync(new { op = "done" }, ct);
    }
}