using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LanFileSync;

internal static class ScriptRunner
{
    internal static string? Run(string script, string workDir, Action<string> log)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "BBK_PreBackup_" + Guid.NewGuid().ToString("N") + ".bat");
        try
        {
            WriteBat(tmp, script);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{tmp}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit();
            if (p == null)
                return "启动备份前脚本失败";
            if (p.ExitCode != 0)
                return $"备份前脚本执行失败(ExitCode {p.ExitCode})";
            return null;
        }
        catch (Exception ex)
        {
            log("执行备份前脚本异常: " + ex.Message);
            return "执行备份前脚本异常: " + ex.Message;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static void WriteBat(string path, string script)
    {
        Encoding enc = Encoding.UTF8;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { }
        File.WriteAllText(path, script, enc);
    }
}