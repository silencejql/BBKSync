using System.IO;

namespace LanFileSync;

public static class FileLister
{
    public static List<FileEntry> ListFiles(string root)
    {
        var list = new List<FileEntry>();
        string rootPath = Path.GetFullPath(root);
        if (!Directory.Exists(rootPath))
            return list;

        foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(file);
                string rel = UploadRules.ToRel(file, rootPath);
                list.Add(new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc.Ticks));
            }
            catch { }
        }

        return list;
    }
}