using System.Text.RegularExpressions;

namespace LanFileSync;

public static class UploadRules
{
    private static readonly Regex TmpRegex = new(@"\.[0-9a-fA-F]{32}\.tmp$", RegexOptions.Compiled);
    public static string ToRel(string path, string root)
        => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string Top(string rel)
    {
        int i = rel.IndexOf('/');
        return i < 0 ? rel : rel.Substring(0, i);
    }

    public static bool HasSegment(string rel, string name)
    {
        foreach (var seg in rel.Split('/'))
        {
            if (string.Equals(seg, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static bool IsBin(string rel)
        => string.Equals(Top(rel), "Bin", StringComparison.OrdinalIgnoreCase);

    public static bool IsSkippedConfig(string rel)
        => !IsBin(rel) && HasSegment(rel, "Config");

    public static bool IsSkippedLog(string rel)
        => HasSegment(rel, "Log");

    public static bool IsSkippedSync(string rel)
        => IsSkippedConfig(rel) || IsSkippedLog(rel);

    public static bool IsTmpFile(string path)
        => TmpRegex.IsMatch(path);
}