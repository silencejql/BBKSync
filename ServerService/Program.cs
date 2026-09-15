using System.IO;
using System.Text.Json;

namespace LanFileSync.ServerService;

internal static class Program
{
    private const string ServiceName = "BBKSyncServer";

    private static ServerHost? _host;

    private static int Main()
    {
        ServerRuntimeOptions options = LoadSettings();

        // 自更新脚本与备份前脚本模板放在 exe 同目录
        ServerSelfUpdater.EnsureBat();
        PreBackupScript.Ensure();

        void Run(CancellationToken ct)
        {
            _host = new ServerHost(ServiceLog.Log, m => ServiceLog.Log("【错误】" + m));
            _host.Start(options);
            while (!ct.IsCancellationRequested)
                Thread.Sleep(500);
            _host.Stop();
        }

        // 由 SCM 启动时作为 Windows 服务运行；直接双击/命令行启动时进入调试模式
        if (ServiceRuntime.TryRunAsService(ServiceName, Run))
            return 0;

        return RunConsole(Run);
    }

    private static int RunConsole(Action<CancellationToken> run)
    {
        Console.Title = "BBK 远端服务（控制台调试模式）";
        Console.WriteLine("BBK 远端服务 - 控制台调试模式（服务未安装时可用此模式调试）");
        Console.WriteLine("按 Q 或 Ctrl+C 停止服务。");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var work = new Thread(() =>
        {
            try { run(cts.Token); }
            catch (Exception ex) { ServiceLog.Log("运行异常: " + ex); }
        });
        work.IsBackground = true;
        work.Start();

        try
        {
            while (work.IsAlive)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Q)
                {
                    cts.Cancel();
                    break;
                }
                Thread.Sleep(100);
            }
            work.Join(TimeSpan.FromSeconds(10));
        }
        catch { }
        return 0;
    }

    private static ServerRuntimeOptions LoadSettings()
    {
        string path = Path.Combine(AppPaths.ExeDir(), "appsettings.json");
        if (!File.Exists(path))
        {
            ServiceLog.Log("appsettings.json 不存在，使用默认配置: " + path);
            return new ServerRuntimeOptions();
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            string rootDir = Constants.DefaultRoot;
            int port = Constants.DefaultPort;
            if (root.TryGetProperty("Server", out var server))
            {
                if (server.TryGetProperty("Root", out var r) && r.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(r.GetString()))
                    rootDir = r.GetString()!;
                if (server.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pv) && pv is >= 1 and <= 65535)
                    port = pv;
            }

            bool beforeSync = true, logRule = true, ignoreEnabled = true, compress = true;
            string backupDest = Constants.DefaultBackupDest, ignores = "副本|copy|-";
            int logDays = 2;
            if (root.TryGetProperty("Backup", out var backup))
            {
                beforeSync = GetBool(backup, "BeforeSync", true);
                backupDest = GetString(backup, "Dest", Constants.DefaultBackupDest);
                logRule = GetBool(backup, "LogRule", true);
                int d = GetInt(backup, "LogDays", 2);
                logDays = d < 1 ? 2 : d;
                ignoreEnabled = GetBool(backup, "IgnoreRegexEnabled", true);
                ignores = GetString(backup, "IgnoreRegexes", "副本|copy|-");
                compress = GetBool(backup, "CompressUpdateZip", true);
            }

            return new ServerRuntimeOptions
            {
                Root = rootDir,
                Port = port,
                BackupBeforeSync = beforeSync,
                BackupDest = backupDest,
                ApplyLogRule = logRule,
                LogRetentionDays = logDays,
                IgnoreRegexEnabled = ignoreEnabled,
                IgnoreRegexes = ignores,
                CompressUpdateZip = compress,
            };
        }
        catch (Exception ex)
        {
            ServiceLog.Log("解析 appsettings.json 失败，使用默认配置: " + ex.Message);
            return new ServerRuntimeOptions();
        }
    }

    private static bool GetBool(JsonElement el, string name, bool fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static int GetInt(JsonElement el, string name, int fallback)
        => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    private static string GetString(JsonElement el, string name, string fallback)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()! : fallback;
}
