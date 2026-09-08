using System.Diagnostics;

namespace LanFileSync;

public static class FreeFormKiller
{
    public static IEnumerable<Process> FindFreeFormProcesses()
    {
        return Process.GetProcesses().Where(p =>
        {
            try
            {
                return p.ProcessName.StartsWith("FreeFormAlways", StringComparison.OrdinalIgnoreCase);
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
        foreach (var p in FindFreeFormProcesses())
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
                killed++;
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }
}