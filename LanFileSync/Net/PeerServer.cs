using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace LanFileSync;

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
    private readonly bool _compressUpdateZip;
    private CancellationTokenSource _cts = new();

    public bool Running { get; private set; }

    public PeerServer(string root, int port, SyncOptions options, Action<string> log, Action<string, long> onFile, Action<int> onTotal,
        Action<string> onError, bool backupBeforeSync, string backupDest, BackupOptions backupOptions,
        bool compressUpdateZip)
    {
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
        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
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

    private async Task CompressAndRemoveFolderAsync(string folderPath, bool compress)
    {
        if (!compress)
            return;
        try
        {
            if (!Directory.Exists(folderPath))
                return;
            string zipPath = folderPath + ".zip";
            _log("正在压缩备份文件夹 ...");
            await Task.Run(() =>
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                ZipHelper.CompressFolder(folderPath, zipPath);
                Directory.Delete(folderPath, recursive: true);
            });
            _log("已压缩为 " + Path.GetFileName(zipPath));
        }
        catch (Exception ex)
        {
            _log("压缩失败: " + ex.Message);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        using var conn = new PeerConnection(tcp);
        try
        {
            var hello = await conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接未发送握手信息");
            string role = hello.GetProperty("role").GetString()!;
            string roleDesc = role switch
            {
                Constants.RolePush => "本机为更新目标",
                Constants.RoleProbe => "读取本机设备配置",
                Constants.RoleTransfer => "本机为传输目标",
                Constants.RoleProcessClose => "关闭远端进程",
                Constants.RoleProcessOpen => "启动远端进程",
                Constants.RoleTransferPull => "本机为传输源(远端拉取)",
                _ => "本机为文件源",
            };
            _log("------------------------------------------------");
            _log($"收到连接(角色: {roleDesc}){tcp.Client.RemoteEndPoint}");

            if (role == Constants.RoleProbe)
            {
                var (name, line) = DeviceConfig.ReadFromRoot(_root);
                await conn.SendJsonAsync(new { op = "cfg", name, line }, CancellationToken.None);
                _log("已返回设备配置");
            }
            else
            {
                if (role == Constants.RolePull)
                {
                    if (hello.TryGetProperty("preBackupBat", out var pb) && pb.GetBoolean())
                    {
                        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
                        if (!File.Exists(batPath) || new FileInfo(batPath).Length == 0)
                        {
                            _log("对方要求先执行备份前脚本，本机 PostgreSQL_Backup.bat 不存在或为空，跳过");
                            await conn.SendJsonAsync(new { op = "bat", ok = true, msg = "本机未配置备份前脚本，跳过" }, CancellationToken.None);
                        }
                        else
                        {
                            _log("对方要求先执行备份前脚本 ...");
                            string? batErr = RunPreBackupScript(_log);
                            if (batErr != null)
                            {
                                _onError(batErr + "，继续备份");
                                await conn.SendJsonAsync(new { op = "bat", ok = false, msg = batErr }, CancellationToken.None);
                            }
                            else
                            {
                                _log("备份前脚本执行完成");
                                await conn.SendJsonAsync(new { op = "bat", ok = true, msg = "已执行备份前脚本" }, CancellationToken.None);
                            }
                        }
                    }
                    await SourceSide.SendManifestAsync(conn, _root, ct);
                    _log("文件清单已发送，等待对方选择需要更新的文件...");

                    var req = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    var paths = req.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();

                    int sent = 0;
                    _log(paths.Count == 0 ? "对方无需更新/备份(所有文件相同)。" : $"对方需要 {paths.Count} 个文件，开始发送...");
                    if (paths.Count > 0 && paths.Count < 10)
                        foreach (var p in paths) _log("  → " + p);
                    await SourceSide.SendRequestedFilesAsync(conn, _root, paths,
                        (_, _) => { if (++sent % 25 == 0 || sent == paths.Count) _log($"已发送 {sent}/{paths.Count} 个文件..."); }, ct);
                    _log("文件发送完成");
                }
                else if (role == Constants.RolePush)
                {
                    var list = new List<FileEntry>();
                    await TargetSide.ReceiveManifestAsync(conn, ct, list);
                    _log($"已收到对方文件清单({list.Count} 项)，按本机规则计算需要更新的文件...");

                    var engine = new SyncEngine(_root, _options);
                    engine.Plan(list);

                    if (engine.NeedList.Count > 0 && _backupBeforeSync && !string.IsNullOrWhiteSpace(_backupDest))
                    {
                        string dest = Path.Combine(_backupDest, $"BBK_推送更新备份_{DateTime.Now:yyyyMMdd}");
                        _log($"对方需要 {engine.NeedList.Count} 个文件，先备份本机 BBK 到 {dest}");
                        try
                        {
                            var be = new BackupEngine(_root, dest, _backupOptions);
                            var files = be.Plan();
                            await be.RunAsync(null, m => _onError("同步前备份跳过: " + m), ct);
                            _log($"同步前备份完成({files.Count} 项)");
                            await CompressAndRemoveFolderAsync(dest, _compressUpdateZip);
                        }
                        catch (ArgumentException)
                        {
                            _log(_backupDest + " 为空或与同步目录相同/位于其内部，跳过同步前备份。");
                        }
                        catch (Exception ex)
                        {
                            await conn.SendJsonAsync(new { op = "err", msg = "目标电脑同步前备份失败: " + ex.Message }, CancellationToken.None);
                            throw;
                        }
                    }

                    _onTotal(engine.NeedList.Count);

                    if (engine.NeedList.Count > 0 && engine.NeedList.Count < 10)
                        foreach (var p in engine.NeedList) _log("  ← " + p);

                    var applyMsgs = new List<string>();
                    await conn.SendJsonAsync(new { op = "need", paths = engine.NeedList }, ct);
                    await engine.ReceiveAndApplyAsync(conn, _onFile, m =>
                    {
                        applyMsgs.Add(m);
                        _onError(m);
                    }, ct);
                    await conn.SendJsonAsync(new { op = "bye", msgs = applyMsgs }, ct);
                    _log("接收并应用完成");
                }
                else if (role == Constants.RoleTransfer)
                {
                    var init = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    string op0 = init.GetProperty("op").GetString()!;
                    if (op0 != "tinit")
                        throw new InvalidOperationException("未知的传输起始消息: " + op0);
                    string itemPath = init.GetProperty("p").GetString()!;
                    bool isDir = init.GetProperty("isDir").GetBoolean();
                    bool sameSkip = init.GetProperty("sameSkip").GetBoolean();
                    bool updateMode = !init.TryGetProperty("update", out var upd) || upd.GetBoolean();
                    string sourceDev = init.TryGetProperty("dev", out var dv) ? dv.GetString() ?? "" : "";
                    // 更新模式备份的是本机(目标)文件，用本机设备名；拷贝模式保留来源设备名
                    string ownName = DeviceConfig.ReadFromRoot(_root).Name;
                    if (string.IsNullOrWhiteSpace(ownName)) ownName = Environment.MachineName;
                    string deviceTag = updateMode ? ownName : (string.IsNullOrWhiteSpace(sourceDev) ? ownName : sourceDev);
                    _log($"收到传输请求(本机为目标，{(updateMode ? "更新模式" : "拷贝模式")})：{itemPath}({(isDir ? "文件夹" : "文件")})...");

                    var list = new List<FileEntry>();
                    await TargetSide.ReceiveManifestAsync(conn, ct, list);
                    _log(updateMode
                        ? $"已收到对方文件清单({list.Count} 项)，按本机相同路径计算需要更新的文件..."
                        : $"已收到对方文件清单({list.Count} 项)，拷贝模式全部保存为重命名副本...");

                    var engine = new TransferEngine(itemPath, isDir, sameSkip, updateMode, deviceTag);
                    engine.Plan(list);

                    if (updateMode && engine.NeedList.Count > 0)
                    {
                        bool ok = engine.BackupItem(_log, m => _onError("备份: " + m));
                        if (!ok)
                        {
                            await conn.SendJsonAsync(new { op = "err", msg = "目标电脑更新前自动备份失败" }, CancellationToken.None);
                            throw new InvalidOperationException("更新前自动备份失败");
                        }
                    }

                    if (!updateMode && engine.NeedList.Count > 0)
                        _log($"拷贝文件将保存到: {engine.EffectiveTarget}");

                    _onTotal(engine.NeedList.Count);
                    await conn.SendJsonAsync(new { op = "need", paths = engine.NeedList }, ct);
                    await engine.ReceiveAndApplyAsync(conn, _log, _onFile, _onError, ct);
                    await conn.SendJsonAsync(new { op = "bye", msgs = engine.TransferMessages }, ct);
                    _log("传输应用完成");
                }
                else if (role == Constants.RoleTransferPull)
                {
                    // 远端请求拉取本机文件（不修改本机文件）
                    var init = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    string op0 = init.GetProperty("op").GetString()!;
                    if (op0 != "tinit")
                        throw new InvalidOperationException("未知的传输起始消息: " + op0);
                    string itemPath = init.GetProperty("p").GetString()!;
                    bool isDir = Directory.Exists(itemPath);
                    bool exists = isDir || File.Exists(itemPath);
                    if (!exists)
                    {
                        _log($"拉取请求的路径不存在: {itemPath}");
                        await conn.SendJsonAsync(new { op = "err", msg = "远端路径不存在: " + itemPath }, CancellationToken.None);
                        return;
                    }
                    _log($"收到拉取请求(本机为源)：{itemPath}({(isDir ? "文件夹" : "文件")})...");

                    // 发送文件清单
                    if (isDir)
                    {
                        if (Directory.Exists(itemPath))
                        {
                            foreach (string file in Directory.EnumerateFiles(itemPath, "*", SearchOption.AllDirectories))
                            {
                                try
                                {
                                    var fi = new FileInfo(file);
                                    await conn.SendJsonAsync(new { op = "f", p = file.Replace('\\', '/'), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        var fi = new FileInfo(itemPath);
                        if (fi.Exists)
                            await conn.SendJsonAsync(new { op = "f", p = itemPath.Replace('\\', '/'), s = fi.Length, t = fi.LastWriteTimeUtc.Ticks }, ct);
                    }
                    await conn.SendJsonAsync(new { op = "mend" }, ct);
                    _log($"文件清单已发送，等待对方选择要拉取的文件...");

                    var req = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    var paths = req.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();

                    int sent = 0;
                    _log(paths.Count == 0 ? "对方无需拉取文件。" : $"对方要拉取 {paths.Count} 个文件，开始发送...");
                    if (paths.Count > 0 && paths.Count < 10)
                        foreach (var p in paths) _log("  → " + p);
                    await TransferSide.SendRequestedFilesAsync(conn, paths,
                        (_, _) => { if (++sent % 25 == 0 || sent == paths.Count) _log($"已发送 {sent}/{paths.Count} 个文件..."); }, ct);
                    _log("文件发送完成");

                    await conn.SendJsonAsync(new { op = "bye", msgs = Array.Empty<string>() }, ct);
                    _log("拉取传输完成");
                }
                else if (role == Constants.RoleUpdate)
                {
                    _log("收到更新程序请求，准备接收更新文件...");
                    await conn.SendJsonAsync(new { op = "ready" }, ct);

                    var exeMsg = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    if (exeMsg.GetProperty("op").GetString() != "exe")
                        throw new InvalidOperationException("期望 exe 消息");
                    long exeSize = exeMsg.GetProperty("s").GetInt64();

                    string workDir = AppPaths.ExeDir();
                    string batPath = Path.Combine(workDir, "Update_BBKSync.bat");
                    string newExePath = Path.Combine(workDir, "BBKSync_New.exe");
                    string curExePath = Environment.ProcessPath ?? Path.Combine(workDir, "BBKSync.exe");

                    using (var fs = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
                    {
                        long remaining = exeSize;
                        var buf = new byte[128 * 1024];
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buf.Length, remaining);
                            await conn.ReadRawAsync(buf, toRead, ct);
                            await fs.WriteAsync(buf.AsMemory(0, toRead), ct);
                            remaining -= toRead;
                        }
                    }
                    _log("更新文件接收完成，正在执行更新脚本...");

                    await conn.SendJsonAsync(new { op = "ok", msg = "文件已接收，即将执行更新" }, ct);
                    try { tcp.Close(); } catch { }

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c \"{batPath}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            };
                            System.Diagnostics.Process.Start(psi);
                        }
                        catch { }
                    });
                }
                else if (role == Constants.RoleProcessClose)
                {
                    var init = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    string op0 = init.GetProperty("op").GetString()!;
                    if (op0 != "aclose")
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
                    await conn.SendJsonAsync(new { op = "ok", msg, killed, remaining }, CancellationToken.None);
                }
                else if (role == Constants.RoleProcessOpen)
                {
                    var init = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    string op0 = init.GetProperty("op").GetString()!;
                    if (op0 != "aopen")
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
                        await conn.SendJsonAsync(new { op = "ok", msg, running }, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _log("启动失败: " + ex.Message);
                        await conn.SendJsonAsync(new { op = "err", msg = ex.Message }, CancellationToken.None);
                    }
                }
                else if (role == Constants.RoleFileList)
                {
                    var init = await conn.RecvJsonAsync(ct)
                        ?? throw new EndOfStreamException("连接已断开");
                    string op0 = init.GetProperty("op").GetString()!;
                    if (op0 != "flst")
                        throw new InvalidOperationException("未知消息: " + op0);
                    string suffixes = init.GetProperty("suffixes").GetString() ?? "";
                    _log($"收到文件列表请求(后缀: {suffixes})...");
                    try
                    {
                        var paths = EnumerateFiles(Constants.DefaultRoot, suffixes);
                        foreach (var path in paths)
                            await conn.SendJsonAsync(new { op = "fpath", path = path }, CancellationToken.None);
                        await conn.SendJsonAsync(new { op = "fend" }, CancellationToken.None);
                        _log($"文件列表完成(共{paths.Count}个文件)");
                    }
                    catch (Exception ex)
                    {
                        _log("文件列表失败: " + ex.Message);
                        await conn.SendJsonAsync(new { op = "err", msg = ex.Message }, CancellationToken.None);
                    }
                }
                else
                {
                    await conn.SendJsonAsync(new { op = "err", msg = "未知角色: " + role }, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log("处理连接失败: " + ex.Message);
        }
        finally
        {
            _log("执行完成，关闭连接");
            try { tcp.Close(); } catch { }
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