using System.IO;

namespace LanFileSync;

internal static class AppPaths
{
    /// <summary>配置与脚本所在文件夹名（与 BBKSync.exe 同级）。</summary>
    internal const string ConfigFolderName = "BBKSyncConfig";

    internal static string ExeDir()
    {
        try
        {
            string? p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p))
            {
                string? dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir))
                    return dir;
            }
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    /// <summary>配置与脚本目录：默认「exe 同级\BBKSyncConfig」，无法创建时退回 exe 同级目录。</summary>
    internal static string ConfigDir()
    {
        string dir = Path.Combine(ExeDir(), ConfigFolderName);
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return ExeDir();
        }
    }

    internal static string ConfigFile(string fileName) => Path.Combine(ConfigDir(), fileName);

    /// <summary>
    /// 把旧版本散放在 exe 同级目录的配置/脚本迁移到 BBKSyncConfig：
    /// 新位置无同名文件时直接移动；两边都有时取修改时间较新的一份，并删除旧位置文件。
    /// </summary>
    internal static void MigrateLegacyFile(string fileName)
    {
        try
        {
            string legacy = Path.Combine(ExeDir(), fileName);
            if (!File.Exists(legacy)) return;
            string current = ConfigFile(fileName);
            if (Path.GetFullPath(legacy) == Path.GetFullPath(current)) return;
            if (!File.Exists(current))
            {
                File.Move(legacy, current);
                return;
            }
            if (File.GetLastWriteTimeUtc(legacy) > File.GetLastWriteTimeUtc(current))
                File.Copy(legacy, current, overwrite: true);
            File.Delete(legacy);
        }
        catch { }
    }

    internal static string ResolveConfigFile(string fileName)
    {
        MigrateLegacyFile(fileName);
        try
        {
            string candidate = ConfigFile(fileName);
            using (File.Open(candidate, FileMode.OpenOrCreate, FileAccess.Write)) { }
            return candidate;
        }
        catch
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Constants.AppDataFolderName);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
    }
}
