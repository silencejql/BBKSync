using System.IO;

namespace LanFileSync;

public sealed class BackupEngine
{
    public string Source { get; }
    public string Dest { get; }
    public BackupOptions Options { get; }

    private List<string>? _plan;

    public BackupEngine(string source, string dest, BackupOptions options)
    {
        Source = Path.GetFullPath(source);
        Dest = Path.GetFullPath(dest);
        Options = options;

        if (string.Equals(Trim(Source), Trim(Dest), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("备份目标不能和源文件夹相同。");
        if (IsInside(Dest, Source))
            throw new ArgumentException("备份目标不能位于要备份的文件夹内部。");
    }

    public IReadOnlyList<string> Plan()
    {
        if (_plan != null)
            return _plan;

        if (!Directory.Exists(Source))
            throw new DirectoryNotFoundException("源文件夹不存在: " + Source);

        DateTime cut = DateTime.UtcNow.AddDays(-Options.LogRetentionDays);
        var list = new List<string>();

        foreach (string file in Directory.EnumerateFiles(Source, "*", SearchOption.AllDirectories))
        {
            try
            {
                string rel = UploadRules.ToRel(file, Source);
                if (UploadRules.IsTmpFile(file))
                    continue;
                if (Options.IsIgnored(rel))
                    continue;

                if (Options.ApplyLogRule && UploadRules.HasSegment(rel, "Log"))
                {
                    var fi = new FileInfo(file);
                    if (fi.LastWriteTimeUtc < cut)
                        continue;
                }

                list.Add(rel);
            }
            catch { }
        }

        _plan = list;
        return list;
    }

    public async Task RunAsync(Action<string, long>? onFile, CancellationToken ct)
    {
        var files = Plan();
        var buf = new byte[Constants.StreamBufferSize];

        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();

            string srcPath = Path.Combine(Source, rel);
            string dstPath = Path.Combine(Dest, rel);

            long size;
            try { size = new FileInfo(srcPath).Length; }
            catch
            {
                onFile?.Invoke(rel, 0);
                continue;
            }

            if (!NeedsCopy(srcPath, dstPath))
            {
                onFile?.Invoke(rel, size);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
            string tmp = dstPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Constants.StreamBufferSize, FileOptions.SequentialScan))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
                {
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }

                File.Move(tmp, dstPath, overwrite: true);
                try { File.SetLastWriteTimeUtc(dstPath, File.GetLastWriteTimeUtc(srcPath)); } catch { }
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            onFile?.Invoke(rel, size);
        }
    }

    private static bool NeedsCopy(string src, string dst)
    {
        var sf = new FileInfo(src);
        if (!sf.Exists)
            return false;

        var df = new FileInfo(dst);
        if (!df.Exists)
            return true;
        if (df.Length != sf.Length)
            return true;

        return Math.Abs(df.LastWriteTimeUtc.Ticks - sf.LastWriteTimeUtc.Ticks) > Constants.TimeTolerance.Ticks;
    }

    private static string Trim(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string inner, string outer)
    {
        string c = Trim(inner) + Path.DirectorySeparatorChar;
        string o = Trim(outer) + Path.DirectorySeparatorChar;
        return c.StartsWith(o, StringComparison.OrdinalIgnoreCase);
    }
}