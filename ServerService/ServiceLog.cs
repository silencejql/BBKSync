using System.IO;

namespace LanFileSync.ServerService;

/// <summary>
/// 服务日志：写入 exe 同目录 logs\server-yyyyMMdd.log（服务无控制台界面）。
/// </summary>
internal static class ServiceLog
{
    private static readonly object _lock = new();
    private static readonly string _logDir = Path.Combine(AppPaths.ExeDir(), "logs");

    public static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_logDir);
                string file = Path.Combine(_logDir, $"server-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { }
        Console.WriteLine(line);
    }
}
