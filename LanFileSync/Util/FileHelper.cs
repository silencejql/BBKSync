using System.IO;

namespace LanFileSync;

public static class FileHelper
{
    /// <summary>将非法文件名字符替换为下划线。</summary>
    public static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>若路径已存在则追加 _1、_2… 后缀直至不存在。</summary>
    public static string UniquePath(string path, bool isDir)
    {
        if (!(isDir ? Directory.Exists(path) : File.Exists(path))) return path;
        string parent = Path.GetDirectoryName(path) ?? "";
        string baseName = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(parent, $"{baseName}_{i}{ext}");
            if (!(isDir ? Directory.Exists(candidate) : File.Exists(candidate))) return candidate;
        }
    }
}
