using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace LanFileSync;

/// <summary>端口可用性诊断：区分「被占用」与「被 Windows 保留」两类监听失败原因。</summary>
public static class PortInspector
{
    private readonly record struct Range(int Start, int End);

    /// <summary>端口是否落在 Windows 的 TCP 排除端口范围内（常见于 Hyper-V / WSL / Docker 动态端口）。</summary>
    public static bool TryGetExcludedRange(int port, out int start, out int end)
    {
        foreach (var range in GetExcludedRanges())
        {
            if (port < range.Start || port > range.End) continue;
            start = range.Start;
            end = range.End;
            return true;
        }
        start = end = 0;
        return false;
    }

    /// <summary>在配置端口之后找一个小范围内未被保留、未被占用的端口；找不到返回 0。</summary>
    public static int SuggestPort(int port)
    {
        var ranges = GetExcludedRanges().ToList();
        var used = new HashSet<int>(IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Select(e => e.Port));
        for (int candidate = port + 1; candidate <= Math.Min(65535, port + 200); candidate++)
        {
            if (ranges.Any(r => candidate >= r.Start && candidate <= r.End)) continue;
            if (used.Contains(candidate)) continue;
            return candidate;
        }
        return 0;
    }

    /// <summary>把监听失败的异常翻译成可操作的提示（含保留范围、建议端口与释放命令）。</summary>
    public static string BuildAccessDeniedMessage(int port)
    {
        int suggested = SuggestPort(port);
        string suggestion = suggested > 0 ? $"建议改用端口 {suggested}" : "请改用其他未被占用的端口";
        if (TryGetExcludedRange(port, out int start, out int end))
        {
            return $"启动服务失败：端口 {port} 位于 Windows 系统保留的端口范围 {start}~{end}，无法监听" +
                   $"（常见于 Hyper-V、WSL 或 Docker 占用的动态端口）。\n\n处理方式（任选其一）：\n" +
                   $"1. {suggestion}，在左侧「端口」中修改后重新启动服务；\n" +
                   "2. 以管理员身份运行 PowerShell，执行：net stop winnat 后执行 net start winnat，释放保留端口后重试。";
        }
        return $"启动服务失败：端口 {port} 无法监听（访问被拒绝）。\n\n" +
               $"可能原因：端口被其他程序独占、被安全软件拦截，或系统保留了该端口。\n{suggestion}，或重启系统后重试。";
    }

    private static List<Range> GetExcludedRanges()
    {
        var ranges = new List<Range>();
        string output;
        try { output = RunNetsh(); }
        catch { return ranges; }

        foreach (string line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(\d+)\s+(\d+)(?:\s+\*)?\s*$");
            if (!match.Success) continue;
            if (int.TryParse(match.Groups[1].Value, out int start) &&
                int.TryParse(match.Groups[2].Value, out int end))
                ranges.Add(new Range(start, end));
        }
        return ranges;
    }

    private static string RunNetsh()
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", "interface ipv4 show excludedportrange protocol=tcp")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process == null) return "";
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        return output;
    }
}
