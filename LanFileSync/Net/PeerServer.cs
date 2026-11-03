using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace LanFileSync;

/// <summary>本机监听服务：按握手角色执行同步、备份、传输、进程管理与程序升级。</summary>
public sealed class PeerServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly SyncOptions _options;
    private readonly bool _backupBeforeSync;
    private readonly string _backupDest;
    private readonly BackupOptions _backupOptions;
    private readonly Action<string> _log;
    private readonly Action<string, long> _onFile;
    private readonly Action<int> _onTotal;
    private readonly Action<string> _onError;
    private readonly Action<bool> _onBusy;
    private readonly bool _compressUpdateZip;
    private int _activeOps;
    private CancellationTokenSource _cts = new();

    public bool Running { get; private set; }

    public PeerServer(string root, int port, SyncOptions options, Action<string> log, Action<string, long> onFile, Action<int> onTotal,
        Action<string> onError, bool backupBeforeSync, string backupDest, BackupOptions backupOptions,
        bool compressUpdateZip, Action<bool> onBusy)
    {
        _onBusy = onBusy;
        _root = root;
        _options = options;
        _backupBeforeSync = backupBeforeSync;
        _backupDest = backupDest;
        _backupOptions = backupOptions;
        _compressUpdateZip = compressUpdateZip;
        _log = log;
        _onFile = onFile;
        _onTotal = onTotal;
        _onError = onError;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        Running = true;

        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
        });
    }

    private string? RunPreBackupScript(Action<string> log)
    {
        string batPath = AppPaths.ConfigFile("PostgreSQL_Backup.bat");
        if (!File.Exists(batPath) || new FileInfo(batPath).Length == 0)
        {
            log("PostgreSQL_Backup.bat 不存在或为空，跳过备份前脚本");
            return null;
        }
        string script = File.ReadAllText(batPath);
        return ScriptRunner.Run(script, AppPaths.ExeDir(), log);
    }

    public void Stop()
    {
        Running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    private async Task CompressAndRemoveFolderAsync(string folderPath, bool compress, Action<string>? log = null)
    {
        log ??= _log;
        if (!compress)
            return;
        try
        {
            if (!Directory.Exists(folderPath))
                return;
            string zipPath = folderPath + ".zip";
            log("正在压缩备份文件夹 ...");
            await Task.Run(() =>
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                ZipHelper.CompressFolder(folderPath, zipPath);
                Directory.Delete(folderPath, recursive: true);
            });
            log("已压缩为 " + Path.GetFileName(zipPath));
        }
        catch (Exception ex)
        {
            log("压缩失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 创建一对日志方法，用于备份等需要向主控端回报进度的流程：
    /// <list type="bullet">
    /// <item>logLocal：仅写本机日志，不回传主控端；</item>
    /// <item>logSync：写本机日志并实时回传主控端显示。</item>
    /// </list>
    /// 后续如需调整某条日志是否回传，在 logLocal(...) 与 logSync(...) 之间切换即可。
    /// </summary>
    private (Action<string> LogLocal, Action<string> LogSync) CreateLogRelay(PeerConnection conn, CancellationToken ct)
    {
        void LogLocal(string m) => _log(m);

        void LogSync(string m)
        {
            _log(m);
            // 同步阻塞写入，保证多条日志按产生顺序到达主控端
            try
            {
                conn.SendJsonAsync(new { op = ProtocolOp.RemoteLog, msgs = new[] { m } }, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log("回传主控端日志失败: " + ex.Message);
            }
        }

        return (LogLocal, LogSync);
    }

    /// <summary>远端(主控端)正在本机执行任务时，通知界面禁用输入与按钮；多个连接用计数汇总。</summary>
    private void SetRemoteBusy(bool busy)
    {
        if (busy)
        {
            if (Interlocked.Increment(ref _activeOps) == 1)
                _onBusy(true);
        }
        else
        {
            if (Interlocked.Decrement(ref _activeOps) <= 0)
            {
                Interlocked.Exchange(ref _activeOps, 0);
                _onBusy(false);
            }
        }
    }

    /// <summary>读取主控端下发的备份过滤规则；未下发时回退到本机设置(兼容旧版本)。</summary>
    private static BackupOptions ParseBackupFilter(JsonElement hello, BackupOptions fallback)
    {
        if (!hello.TryGetProperty("backupFilter", out var f) || f.ValueKind != JsonValueKind.Object || f.ValueKind == JsonValueKind.Null)
            return fallback;
        bool logRule = !f.TryGetProperty("logRule", out var lr) || lr.ValueKind != JsonValueKind.False;
        int days = f.TryGetProperty("logDays", out var ld) && ld.ValueKind == JsonValueKind.Number ? ld.GetInt32() : fallback.LogRetentionDays;
        var ignores = new List<string>();
        if (f.TryGetProperty("ignore", out var ig) && ig.ValueKind == JsonValueKind.Array)
            foreach (var x in ig.EnumerateArray())
            {
                if (x.GetString() is { Length: > 0 } s)
                    ignores.Add(s);
            }
        return new BackupOptions { ApplyLogRule = logRule, LogRetentionDays = days, IgnoreRegexes = ignores };
    }

    /// <summary>处理单个客户端连接：解析握手、标记远端忙碌，再按角色分派。</summary>
    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using var conn = new PeerConnection(tcp);
        bool remoteWork = false;
        try
        {
            var hello = await conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接未发送握手信息");
            string role = hello.GetProperty("role").GetString()!;
            _log("------------------------------------------------");
            _log($"收到连接(角色: {DescribeRole(role)}){tcp.Client.RemoteEndPoint}");

            // 更新规则由主控端下发：Bin 整体替换、同步前备份均按主控端勾选执行
            bool clientBinReplace = hello.TryGetProperty("binReplace", out var brEl) && brEl.ValueKind == JsonValueKind.True;
            bool clientBackupBeforeSync = hello.TryGetProperty("backupBeforeSync", out var bbEl)
                ? bbEl.ValueKind == JsonValueKind.True
                : _backupBeforeSync;

            remoteWork = role is Constants.RolePush or Constants.RolePull or Constants.RoleTransfer
                or Constants.RoleTransferPull or Constants.RoleUpdate;
            if (remoteWork)
            {
                SetRemoteBusy(true);
                _log("远端正在本机执行任务，界面操作已暂时禁用");
            }

            await DispatchRoleAsync(conn, tcp, hello, role, clientBinReplace, clientBackupBeforeSync, ct);
        }
        catch (OperationCanceledException)
        {
            _log("主控端已取消操作，本机已中止本次任务");
        }
        catch (EndOfStreamException)
        {
            _log("与主控端的连接已断开(可能是主控端取消或网络中断)，本机已中止本次任务");
        }
        catch (IOException ex)
        {
            _log("与主控端的连接中断(可能是主控端取消或网络中断)，本机已中止本次任务：" + ex.Message);
        }
        catch (Exception ex)
        {
            _log("处理连接失败: " + ex.Message);
            _onError(ex.Message);
        }
        finally
        {
            if (remoteWork)
                SetRemoteBusy(false);
            _log("执行完成，关闭连接");
            try { tcp.Close(); } catch { }
        }
    }

    /// <summary>握手角色对应的中文说明，仅用于日志。</summary>
    private static string DescribeRole(string role) => role switch
    {
        Constants.RolePush => "本机为更新目标",
        Constants.RoleProbe => "读取本机设备配置",
        Constants.RoleTransfer => "本机为传输目标",
        Constants.RoleProcessClose => "关闭远端进程",
        Constants.RoleProcessOpen => "启动远端进程",
        Constants.RoleTransferPull => "本机为传输源(远端拉取)",
        Constants.RoleUpdate => "接收程序更新",
        _ => "本机为文件源",
    };

    /// <summary>按握手角色分派到对应的处理流程。</summary>
    private async Task DispatchRoleAsync(PeerConnection conn, TcpClient tcp, JsonElement hello, string role,
        bool clientBinReplace, bool clientBackupBeforeSync, CancellationToken ct)
    {
        switch (role)
        {
            case Constants.RoleProbe:
                await HandleProbeAsync(conn);
                break;
            case Constants.RolePull:
                await HandlePullSourceAsync(conn, hello, ct);
                break;
            case Constants.RolePush:
                await HandlePushTargetAsync(conn, hello, clientBinReplace, clientBackupBeforeSync, ct);
                break;
            case Constants.RoleTransfer:
                await HandleTransferTargetAsync(conn, ct);
                break;
            case Constants.RoleTransferPull:
                await HandleTransferPullAsync(conn, ct);
                break;
            case Constants.RoleUpdate:
                await HandleProgramUpdateAsync(conn, tcp, ct);
                break;
            case Constants.RoleProcessClose:
                await HandleProcessCloseAsync(conn, ct);
                break;
            case Constants.RoleProcessOpen:
                await HandleProcessOpenAsync(conn, ct);
                break;
            case Constants.RoleFileList:
                await HandleFileListAsync(conn, ct);
                break;
            default:
                await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = "未知角色: " + role }, CancellationToken.None);
                break;
        }
    }

    private async Task HandleProbeAsync(PeerConnection conn)
    {
        var (name, line) = DeviceConfig.ReadFromRoot(_root);
        await conn.SendJsonAsync(new { op = ProtocolOp.DeviceConfig, name, line }, CancellationToken.None);
        _log("已返回设备配置");
    }

    /// <summary>本机作为文件源：按需执行备份前脚本，发送(可预筛选的)文件清单，再发送主控端请求的文件。</summary>
    private async Task HandlePullSourceAsync(PeerConnection conn, JsonElement hello, CancellationToken ct)
    {
        await RunPreBackupScriptIfRequestedAsync(conn, hello);
        // 备份时由主控端下发 Log 日期/忽略规则，本机先筛选再发送，减少传输文件数
        BackupOptions? backupFilter = hello.TryGetProperty("backupFilter", out var bfEl) && bfEl.ValueKind == JsonValueKind.Object
            ? ParseBackupFilter(hello, _backupOptions)
            : null;
        await SourceSide.SendManifestAsync(conn, _root, ct, backupFilter, _log);
        _log("文件清单已发送，等待远端选择需要更新的文件...");

        var req = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        var paths = ReadRequestedPaths(req);

        LogRequestedPaths(paths, "远端无需更新/备份(所有文件相同)。", "远端需要");
        await SourceSide.SendRequestedFilesAsync(conn, _root, paths, CreateSendProgressReporter(paths), ct);
        _log("文件发送完成");
    }

    /// <summary>主控端要求时执行本机 PostgreSQL 备份前脚本，并把执行结果回传。</summary>
    private async Task RunPreBackupScriptIfRequestedAsync(PeerConnection conn, JsonElement hello)
    {
        if (!hello.TryGetProperty("preBackupBat", out var pb) || !pb.GetBoolean())
            return;

        string batPath = AppPaths.ConfigFile("PostgreSQL_Backup.bat");
        if (!File.Exists(batPath) || new FileInfo(batPath).Length == 0)
        {
            _log("远端要求先执行备份前脚本，本机 PostgreSQL_Backup.bat 不存在或为空，跳过");
            await conn.SendJsonAsync(new { op = ProtocolOp.Bat, ok = true, msg = "本机未配置备份前脚本，跳过" }, CancellationToken.None);
            return;
        }

        _log("远端要求先执行备份前脚本 ...");
        string? batErr = RunPreBackupScript(_log);
        if (batErr != null)
        {
            _onError(batErr + "，继续备份");
            await conn.SendJsonAsync(new { op = ProtocolOp.Bat, ok = false, msg = batErr }, CancellationToken.None);
        }
        else
        {
            _log("备份前脚本执行完成");
            await conn.SendJsonAsync(new { op = ProtocolOp.Bat, ok = true, msg = "已执行备份前脚本" }, CancellationToken.None);
        }
    }

    /// <summary>本机作为更新目标：按主控端规则计算差异，必要时先备份，再接收并应用文件。</summary>
    private async Task HandlePushTargetAsync(PeerConnection conn, JsonElement hello, bool clientBinReplace,
        bool clientBackupBeforeSync, CancellationToken ct)
    {
        var list = new List<FileEntry>();
        await TargetSide.ReceiveManifestAsync(conn, ct, list);
        _log($"已收到远端文件清单({list.Count} 项)，按主控端下发的更新规则计算需要更新的文件...");

        // 规则以主控端勾选为准(收到老版本连接时回退本机设置)
        var clientOptions = hello.TryGetProperty("binReplace", out _)
            ? new SyncOptions { FullReplaceBin = clientBinReplace }
            : _options;
        var clientBackupOptions = ParseBackupFilter(hello, _backupOptions);
        var engine = new SyncEngine(_root, clientOptions);
        engine.Plan(list);

        if (engine.NeedList.Count == 0)
            _log("本机没有需要更新的文件：现有文件与主控端清单完全一致。");

        if (engine.NeedList.Count > 0 && clientBackupBeforeSync && !string.IsNullOrWhiteSpace(_backupDest))
            await BackupLocalBeforeApplyAsync(conn, engine.NeedList.Count, clientBackupOptions, ct);

        _onTotal(engine.NeedList.Count);
        if (engine.NeedList.Count > 0 && engine.NeedList.Count < 10)
            foreach (var p in engine.NeedList) _log("  ← " + p);

        var applyMsgs = new List<string>();
        await conn.SendJsonAsync(new { op = ProtocolOp.Need, paths = engine.NeedList }, ct);
        await engine.ReceiveAndApplyAsync(conn, _onFile, m =>
        {
            applyMsgs.Add(m);
            _onError(m);
        }, ct);
        await conn.SendJsonAsync(new { op = ProtocolOp.Bye, msgs = applyMsgs, files = engine.Applied }, ct);
        _log("接收并应用完成");
    }

    /// <summary>更新前按主控端规则备份本机 BBK；备份过程日志实时回传主控端。</summary>
    private async Task BackupLocalBeforeApplyAsync(PeerConnection conn, int needCount, BackupOptions options, CancellationToken ct)
    {
        // 备份阶段日志：logSync 实时回传主控端，logLocal 仅写本机
        var (logLocal, logSync) = CreateLogRelay(conn, ct);
        string dest = Path.Combine(_backupDest, $"BBK_AutoBackup_{DateTime.Now:yyyyMMdd}");
        logSync($"远端需要 {needCount} 个文件，先备份本机 BBK 到 {dest}");
        try
        {
            var be = new BackupEngine(_root, dest, options);
            var files = be.Plan();
            await be.RunAsync(null, m =>
            {
                string msg = "同步前备份跳过: " + m;
                _onError(msg);
                logSync(msg);
            }, ct);
            logSync($"同步前备份完成({files.Count} 项)");
            await CompressAndRemoveFolderAsync(dest, _compressUpdateZip, logSync);
        }
        catch (ArgumentException)
        {
            logSync(_backupDest + " 为空或与同步目录相同/位于其内部，跳过同步前备份。");
        }
        catch (Exception ex)
        {
            logLocal("同步前备份失败: " + ex.Message);
            await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = "目标电脑同步前备份失败: " + ex.Message }, CancellationToken.None);
            throw;
        }
    }

    /// <summary>本机作为传输目标：读取传输请求与文件清单，按模式备份原文件后接收覆盖。</summary>
    private async Task HandleTransferTargetAsync(PeerConnection conn, CancellationToken ct)
    {
        var init = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op0 = init.GetProperty("op").GetString()!;
        if (op0 != ProtocolOp.TransferInit)
            throw new InvalidOperationException("未知的传输起始消息: " + op0);
        string itemPath = init.GetProperty("p").GetString()!;
        bool isDir = init.GetProperty("isDir").GetBoolean();
        bool sameSkip = init.GetProperty("sameSkip").GetBoolean();
        bool updateMode = !init.TryGetProperty("update", out var upd) || upd.GetBoolean();
        string sourceDev = init.TryGetProperty("dev", out var dv) ? dv.GetString() ?? "" : "";
        bool deleteSame = init.TryGetProperty("delSame", out var ds) && ds.GetBoolean();
        // 更新模式备份的是本机(目标)文件，用本机设备名；拷贝模式保留来源设备名
        string ownName = DeviceConfig.ReadFromRoot(_root).Name;
        if (string.IsNullOrWhiteSpace(ownName)) ownName = Environment.MachineName;
        string deviceTag = updateMode ? ownName : (string.IsNullOrWhiteSpace(sourceDev) ? ownName : sourceDev);
        _log($"收到传输请求(本机为目标，{(updateMode ? "更新模式" : "拷贝模式")})：{itemPath}({(isDir ? "文件夹" : "文件")})...");

        var list = new List<FileEntry>();
        await TargetSide.ReceiveManifestAsync(conn, ct, list);
        _log(updateMode
            ? $"已收到远端文件清单({list.Count} 项)，按本机相同路径计算需要更新的文件..."
            : $"已收到远端文件清单({list.Count} 项)，拷贝模式全部保存为重命名副本...");

        var engine = new TransferEngine(itemPath, isDir, sameSkip, updateMode, deviceTag, deleteSame);
        engine.Plan(list);

        if (engine.NeedList.Count == 0)
            _log("本机没有需要写入的文件：目标文件与主控端清单一致。");

        if (updateMode && engine.NeedList.Count > 0)
            await BackupTransferTargetAsync(conn, engine, ct);

        if (!updateMode && engine.NeedList.Count > 0)
            _log($"拷贝文件将保存到: {engine.EffectiveTarget}");

        _onTotal(engine.NeedList.Count);
        await conn.SendJsonAsync(new { op = ProtocolOp.Need, paths = engine.NeedList }, ct);
        await engine.ReceiveAndApplyAsync(conn, _log, _onFile, _onError, ct);
        await conn.SendJsonAsync(new { op = ProtocolOp.Bye, msgs = engine.TransferMessages }, ct);
        _log("传输应用完成");
    }

    /// <summary>更新模式覆盖前备份目标文件；失败时回传错误并中止本次传输。</summary>
    private async Task BackupTransferTargetAsync(PeerConnection conn, TransferEngine engine, CancellationToken ct)
    {
        // 备份阶段日志：logSync 实时回传主控端，logLocal 仅写本机
        var (logLocal, logSync) = CreateLogRelay(conn, ct);
        string? backupErr = null;
        bool ok = engine.BackupItem(logSync, m =>
        {
            _onError("备份: " + m);
            backupErr = m;
        });
        if (ok) return;

        logLocal("更新前自动备份失败: " + backupErr);
        await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = "目标电脑更新前自动备份失败: " + backupErr }, CancellationToken.None);
        throw new InvalidOperationException("更新前自动备份失败");
    }

    /// <summary>本机作为传输源：只发送主控端请求的文件，不修改本机文件。</summary>
    private async Task HandleTransferPullAsync(PeerConnection conn, CancellationToken ct)
    {
        var init = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op0 = init.GetProperty("op").GetString()!;
        if (op0 != ProtocolOp.TransferInit)
            throw new InvalidOperationException("未知的传输起始消息: " + op0);
        string itemPath = init.GetProperty("p").GetString()!;
        bool isDir = Directory.Exists(itemPath);
        bool exists = isDir || File.Exists(itemPath);
        if (!exists)
        {
            _log($"拉取请求的路径不存在: {itemPath}");
            await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = "远端路径不存在: " + itemPath }, CancellationToken.None);
            return;
        }
        _log($"收到拉取请求(本机为源)：{itemPath}({(isDir ? "文件夹" : "文件")})...");

        await SendLocalManifestAsync(conn, itemPath, isDir, ct);
        _log("文件清单已发送，等待远端选择要拉取的文件...");

        var req = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        var paths = ReadRequestedPaths(req);

        LogRequestedPaths(paths, "远端无需拉取文件。", "远端要拉取");
        await TransferSide.SendRequestedFilesAsync(conn, paths, CreateSendProgressReporter(paths), ct);
        _log("文件发送完成");

        await conn.SendJsonAsync(new { op = ProtocolOp.Bye, msgs = Array.Empty<string>() }, ct);
        _log("拉取传输完成");
    }

    /// <summary>发送本机文件清单：文件夹递归列举，单文件仅发送自身。</summary>
    private static async Task SendLocalManifestAsync(PeerConnection conn, string itemPath, bool isDir, CancellationToken ct)
    {
        if (isDir)
        {
            foreach (string file in Directory.EnumerateFiles(itemPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    await conn.SendJsonAsync(new { op = ProtocolOp.ManifestEntry, p = file.Replace('\\', '/'), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
                }
                catch { }
            }
        }
        else
        {
            var fi = new FileInfo(itemPath);
            if (fi.Exists)
                await conn.SendJsonAsync(new { op = ProtocolOp.ManifestEntry, p = itemPath.Replace('\\', '/'), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
        }
        await conn.SendJsonAsync(new { op = ProtocolOp.ManifestEnd }, ct);
    }

    /// <summary>读取 req 帧中的文件相对路径列表。</summary>
    private static List<string> ReadRequestedPaths(JsonElement request)
        => request.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();

    /// <summary>输出本次请求的文件数量，少量文件时逐条列出。</summary>
    private void LogRequestedPaths(IReadOnlyList<string> paths, string emptyMessage, string needPrefix)
    {
        _log(paths.Count == 0 ? emptyMessage : $"{needPrefix} {paths.Count} 个文件，开始发送...");
        if (paths.Count > 0 && paths.Count < 10)
            foreach (var p in paths) _log("  → " + p);
    }

    /// <summary>发送进度回调：文件较多时每约 10% 输出一次已发送数量。</summary>
    private Action<string, long> CreateSendProgressReporter(IReadOnlyList<string> paths)
    {
        int sent = 0;
        int reportEvery = paths.Count > 10 ? paths.Count / 10 : (paths.Count > 0 ? 1 : 0);
        return (_, _) =>
        {
            if (reportEvery > 0 && (++sent % reportEvery == 0 || sent == paths.Count))
                _log($"已发送 {sent}/{paths.Count} 个文件...");
        };
    }

    /// <summary>接收程序升级文件，落盘为 BBKSync_New.exe 后通知主控端并延迟启动更新脚本。</summary>
    private async Task HandleProgramUpdateAsync(PeerConnection conn, TcpClient tcp, CancellationToken ct)
    {
        _log("收到更新程序请求，准备接收更新文件...");
        await conn.SendJsonAsync(new { op = ProtocolOp.Ready }, ct);

        var exeMsg = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        if (exeMsg.GetProperty("op").GetString() != ProtocolOp.Exe)
            throw new InvalidOperationException("期望 exe 消息");
        long exeSize = exeMsg.GetProperty("s").GetInt64();
        long? exeMtime = exeMsg.TryGetProperty("t", out var tEl) ? tEl.GetInt64() : null;

        string workDir = AppPaths.ExeDir();
        string newExePath = Path.Combine(workDir, "BBKSync_New.exe");
        await ReceiveExeFileAsync(conn, newExePath, exeSize, exeMtime, ct);
        _log("更新文件接收完成，正在执行更新脚本...");

        await conn.SendJsonAsync(new { op = ProtocolOp.Ok, msg = "文件已接收，即将执行更新" }, ct);
        try { tcp.Close(); } catch { }

        LaunchUpdateScriptAfterDelay(AppPaths.ConfigFile("Update_BBKSync.bat"));
    }

    /// <summary>接收升级 exe 的原始字节并按远端修改时间落盘。</summary>
    private static async Task ReceiveExeFileAsync(PeerConnection conn, string newExePath, long size, long? mtime, CancellationToken ct)
    {
        using (var fs = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
        {
            long remaining = size;
            var buf = new byte[128 * 1024];
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                await conn.ReadRawAsync(buf, toRead, ct);
                await fs.WriteAsync(buf.AsMemory(0, toRead), ct);
                remaining -= toRead;
            }
        }
        if (mtime.HasValue)
            try { File.SetLastWriteTimeUtc(newExePath, new DateTime(mtime.Value, DateTimeKind.Utc)); } catch { }
    }

    /// <summary>延迟 1 秒后启动更新脚本，给当前连接留出关闭时间。</summary>
    private static void LaunchUpdateScriptAfterDelay(string batPath)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
            }
            catch { }
        });
    }

    private async Task HandleProcessCloseAsync(PeerConnection conn, CancellationToken ct)
    {
        var init = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op0 = init.GetProperty("op").GetString()!;
        if (op0 != ProtocolOp.ProcessClose)
            throw new InvalidOperationException("未知消息: " + op0);
        var namesArr = init.GetProperty("names").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string namesDisplay = namesArr.Length > 0 ? string.Join(", ", namesArr) : FreeFormKiller.ProcessPrefixAsterisk;
        _log($"收到关闭请求(进程名: {namesDisplay})...");
        int killed = FreeFormKiller.KillByNames(namesArr);
        int remaining = FreeFormKiller.FindProcessesByNames(namesArr).Count();
        string msg = killed > 0
            ? (remaining > 0 ? $"{namesDisplay}已关闭，剩余{remaining}个同类进程运行中" : $"{namesDisplay}已关闭")
            : $"{namesDisplay}未找到或已关闭";
        _log(msg);
        await conn.SendJsonAsync(new { op = ProtocolOp.Ok, msg, killed, remaining }, CancellationToken.None);
    }

    private async Task HandleProcessOpenAsync(PeerConnection conn, CancellationToken ct)
    {
        var init = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op0 = init.GetProperty("op").GetString()!;
        if (op0 != ProtocolOp.ProcessOpen)
            throw new InvalidOperationException("未知消息: " + op0);
        string exePath = init.GetProperty("path").GetString()!;
        string fileName = Path.GetFileNameWithoutExtension(exePath);
        string exeName = new[] { "Always", "xmlFreeForm" }.Any(name => exePath.Contains(name, StringComparison.CurrentCultureIgnoreCase)) ? "FreeFormsAlways" : "";
        _log($"收到启动请求：{exePath}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            };
            Process.Start(psi);
            int running = FreeFormKiller.CountByName(exeName.Length > 0 ? exeName : fileName);
            string msg = $"{fileName}程序已启动，当前{running}个进程运行中";
            _log(msg);
            await conn.SendJsonAsync(new { op = ProtocolOp.Ok, msg, running }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log("启动失败: " + ex.Message);
            await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = ex.Message }, CancellationToken.None);
        }
    }

    private async Task HandleFileListAsync(PeerConnection conn, CancellationToken ct)
    {
        var init = await conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op0 = init.GetProperty("op").GetString()!;
        if (op0 != ProtocolOp.FileList)
            throw new InvalidOperationException("未知消息: " + op0);
        string suffixes = init.GetProperty("suffixes").GetString() ?? "";
        _log($"收到文件列表请求(后缀: {suffixes})...");
        try
        {
            var paths = EnumerateFiles(Constants.DefaultRoot, suffixes);
            foreach (var path in paths)
                await conn.SendJsonAsync(new { op = ProtocolOp.FileListEntry, path = path }, CancellationToken.None);
            await conn.SendJsonAsync(new { op = ProtocolOp.FileListEnd }, CancellationToken.None);
            _log($"文件列表完成(共{paths.Count}个文件)");
        }
        catch (Exception ex)
        {
            _log("文件列表失败: " + ex.Message);
            await conn.SendJsonAsync(new { op = ProtocolOp.Error, msg = ex.Message }, CancellationToken.None);
        }
    }

    public void Dispose() => Stop();

    private static string[] ParseSuffixes(string suffixes)
    {
        if (string.IsNullOrWhiteSpace(suffixes))
            return new[] { "xmlFreeForm" };

        return suffixes.Split(new[] { ',', '，', ';', '；', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim().TrimStart('.').Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasSuffix(string filePath, IReadOnlyList<string> suffixes)
    {
        string fileName = Path.GetFileName(filePath);
        string ext = Path.GetExtension(filePath).TrimStart('.');
        for (int i = 0; i < suffixes.Count; i++)
        {
            string suffix = suffixes[i];
            if (string.IsNullOrEmpty(suffix))
                continue;
            if (fileName.Contains(suffix, StringComparison.OrdinalIgnoreCase) ||
                ext.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static List<string> EnumerateFiles(string root, string suffixes)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"目录不存在: {root}");

        var patterns = ParseSuffixes(suffixes);
        var result = new List<string>();
        var stack = new Stack<string>(new[] { root });

        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    if (HasSuffix(file, patterns))
                        result.Add(file);
                }
                foreach (var subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                    stack.Push(subDir);
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }
}
