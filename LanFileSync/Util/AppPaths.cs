using System.IO;

namespace LanFileSync;

internal static class AppPaths
{
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

    internal static string ResolveFile(string fileName)
    {
        string exeDir = ExeDir();
        try
        {
            if (!Directory.Exists(exeDir))
                Directory.CreateDirectory(exeDir);
            string candidate = Path.Combine(exeDir, fileName);
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