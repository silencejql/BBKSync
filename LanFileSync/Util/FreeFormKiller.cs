using System.Diagnostics;

namespace LanFileSync;

public static class FreeFormKiller
{
    public static string ProcessPrefix { get; set; } = "FreeFormAlways";

    public static string ProcessPrefixAsterisk => ProcessPrefix + "*";

    public static IEnumerable<Process> FindFreeFormProcesses()
    {
        string prefix = ProcessPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            return Array.Empty<Process>();
        return Process.GetProcesses().Where(p =>
        {
            try
            {
                return p.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }

    public static int KillAll()
    {
        int killed = 0;
        var procs = FindFreeFormProcesses().ToList();
        foreach (var p in procs)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(5000))
                    killed += KillByTaskKill(p.Id);
                else
                    killed++;
                p.Dispose();
            }
            catch
            {
                try { p.Dispose(); } catch { }
            }
        }
        return killed;
    }

    private static int KillByTaskKill(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 ? 1 : 0;
        }
        catch { return 0; }
    }
}