using System.IO;

namespace LanFileSync;

/// <summary>
/// 生成服务端自更新批处理 Update_BBKSync.bat。
/// 主控端推送新 exe 后，服务端收到的文件落盘为 BBKSync_New.exe，
/// 由该脚本关闭当前进程、替换 exe、重新启动。
/// </summary>
internal static class ServerSelfUpdater
{
    /// <summary>确保更新脚本存在并按当前 exe 名称刷新内容。</summary>
    /// <param name="launchArgs">替换后启动新 exe 时附带的命令行参数（如 --minimized）；空字符串表示不带参数。</param>
    public static void EnsureBat(string launchArgs = "")
    {
        string batPath = Path.Combine(AppPaths.ExeDir(), "Update_BBKSync.bat");
        string exeDir = AppPaths.ExeDir();
        string exeName = Path.GetFileName(Environment.ProcessPath ?? "BBKSync.exe");
        string startArgs = string.IsNullOrWhiteSpace(launchArgs) ? "" : " " + launchArgs.Trim();
        string content =
            "@echo off\r\n" +
            "chcp 65001 >nul 2>&1\r\n" +
            "cd /d \"" + exeDir + "\"\r\n" +
            "if not exist BBKSync_New.exe exit\r\n" +
            "echo 等待关闭 " + exeName + " ...\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            "taskkill /f /im " + exeName + " >nul 2>&1\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "del /f /q \"" + exeName + "\" >nul 2>&1\r\n" +
            "ren BBKSync_New.exe " + exeName + "\r\n" +
            "start \"\" \"" + exeDir + "\\" + exeName + "\"" + startArgs + "\r\n";
        File.WriteAllText(batPath, content);
    }
}
