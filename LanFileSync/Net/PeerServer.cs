using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

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
    private CancellationTokenSource _cts = new();

    public bool Running { get; private set; }

    public PeerServer(string root, int port, SyncOptions options, Action<string> log, Action<string, long> onFile, Action<int> onTotal,
        Action<string> onError, bool backupBeforeSync, string backupDest, BackupOptions backupOptions)
    {
        _root = root;
        _options = options;
        _backupBeforeSync = backupBeforeSync;
        _backupDest = backupDest;
        _backupOptions = backupOptions;
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

    public void Stop()
    {
        Running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    private async Task CompressAndRemoveFolderAsync(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return;
            string zipPath = folderPath + ".zip";
            _log("正在压缩备份文件夹 ...");
            await Task.Run(() =>
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Optimal, false);
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
            _log($"收到连接（角色: {(role == Constants.RolePush ? "本机为更新目标" : "本机为文件源")}）{tcp.Client.RemoteEndPoint}");

            if (role == Constants.RolePull)
            {
                await SourceSide.SendManifestAsync(conn, _root, ct);
                _log("文件清单已发送，等待对方选择需要更新的文件...");

                var req = await conn.RecvJsonAsync(ct)
                    ?? throw new EndOfStreamException("连接已断开");
                var paths = req.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
                _log($"对方需要 {paths.Count} 个文件，开始发送...");

                int sent = 0;
                await SourceSide.SendRequestedFilesAsync(conn, _root, paths,
                    (_ , _) => { if (++sent % 25 == 0 || sent == paths.Count) _log($"已发送 {sent}/{paths.Count} 个文件..."); }, ct);
                _log("文件发送完成");
            }
            else if (role == Constants.RolePush)
            {
                var list = new List<FileEntry>();
                await TargetSide.ReceiveManifestAsync(conn, ct, list);
                _log($"已收到对方文件清单（{list.Count} 项），按本机规则计算需要更新的文件...");

                if (_backupBeforeSync && !string.IsNullOrWhiteSpace(_backupDest))
                {
                    string dest = Path.Combine(_backupDest, $"BBK_推送更新备份_{DateTime.Now:yyyyMMdd}");
                    _log($"同步前先备份本机 BBK 到{dest}");
                    try
                    {
                        var be = new BackupEngine(_root, dest, _backupOptions);
                        var files = be.Plan();
                        await be.RunAsync(null, ct);
                        _log($"同步前备份完成（{files.Count} 项）");
                        //await CompressAndRemoveFolderAsync(dest);
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

                var engine = new SyncEngine(_root, _options);
                engine.Plan(list);
                _onTotal(engine.NeedList.Count);

                await conn.SendJsonAsync(new { op = "need", paths = engine.NeedList }, ct);
                await engine.ReceiveAndApplyAsync(conn, _onFile, _onError, ct);
                await conn.SendJsonAsync(new { op = "bye" }, ct);
                _log("接收并应用完成");
            }
            else
            {
                await conn.SendJsonAsync(new { op = "err", msg = "未知角色: " + role }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log("处理连接失败: " + ex.Message);
        }
        finally
        {
            try { tcp.Close(); } catch { }
        }
    }

    public void Dispose() => Stop();
}