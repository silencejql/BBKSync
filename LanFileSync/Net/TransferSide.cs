using System.IO;

namespace LanFileSync;

public static class TransferSide
{
    public static async Task SendTransferManifestAsync(PeerConnection peer, string itemPath, bool isDir, bool sameSkip, bool killFreeForm, CancellationToken ct)
    {
        await peer.SendJsonAsync(new { op = "tinit", p = ToFwdSlash(itemPath), isDir, sameSkip, killFreeForm }, ct);

        if (isDir)
        {
            foreach (string file in Directory.EnumerateFiles(itemPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    await peer.SendJsonAsync(new { op = "f", p = ToFwdSlash(file), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
                }
                catch { }
            }
        }
        else
        {
            var fi = new FileInfo(itemPath);
            if (fi.Exists)
                await peer.SendJsonAsync(new { op = "f", p = ToFwdSlash(itemPath), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
        }

        await peer.SendJsonAsync(new { op = "mend" }, ct);
    }

    public static async Task SendRequestedFilesAsync(
        PeerConnection peer,
        IReadOnlyList<string> paths,
        Action<string, long>? onFile,
        CancellationToken ct)
    {
        var buf = new byte[128 * 1024];

        foreach (var path in paths)
        {
            string file = path.Replace('/', Path.DirectorySeparatorChar);
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
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

        await peer.SendJsonAsync(new { op = "done" }, ct);
    }

    private static string ToFwdSlash(string path) => path.Replace('\\', '/');
}