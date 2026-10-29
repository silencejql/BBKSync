using System.IO;

namespace LanFileSync;

public static class SourceSide
{
    /// <summary>
    /// 发送文件清单。filter 非空时按主控端下发的备份规则(Log 日期、忽略正则)先在本机筛选，
    /// 被排除的文件不再传输，减少网络传输量。
    /// </summary>
    public static async Task SendManifestAsync(PeerConnection peer, string root, CancellationToken ct,
        BackupOptions? filter = null, Action<string>? log = null)
    {
        var files = FileLister.ListFiles(root);
        int excluded = 0;
        foreach (var f in files)
        {
            if (filter != null && ShouldExclude(f, filter))
            {
                excluded++;
                continue;
            }
            await peer.SendJsonAsync(new { op = "f", p = f.RelPath, s = f.Size, t = f.MTimeUtcTicks }, ct);
        }
        if (filter != null)
        {
            log?.Invoke(excluded > 0
                ? $"已按主控端备份规则排除 {excluded} 个文件(Log 日期/忽略规则)，实际发送 {files.Count - excluded} 项。"
                : $"主控端备份规则未排除文件，共发送 {files.Count} 项。");
        }
        await peer.SendJsonAsync(new { op = "mend" }, ct);
    }

    /// <summary>与本地备份引擎相同的排除口径：临时文件、忽略规则、Log 保留天数。</summary>
    private static bool ShouldExclude(FileEntry f, BackupOptions o)
    {
        if (UploadRules.IsTmpFile(f.RelPath))
            return true;
        if (o.IsIgnored(f.RelPath))
            return true;
        if (o.ApplyLogRule && UploadRules.HasSegment(f.RelPath, "Log"))
        {
            if (o.LogRetentionDays <= 0)
                return true;
            DateTime logCut = DateTime.Today.AddDays(-(o.LogRetentionDays - 1));
            if (new DateTime(f.MTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().Date < logCut)
                return true;
        }
        return false;
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