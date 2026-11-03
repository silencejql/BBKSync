using System.IO;

namespace LanFileSync;

/// <summary>更新目标端：接收远端文件清单与数据帧。</summary>
public static class TargetSide
{
    public static async Task ReceiveManifestAsync(PeerConnection peer, CancellationToken ct, List<FileEntry> output)
    {
        while (true)
        {
            var frame = await peer.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");

            string op = frame.GetProperty("op").GetString()!;
            if (op == ProtocolOp.ManifestEnd)
                return;
            if (op == ProtocolOp.Cancel)
                throw new OperationCanceledException("主控端已取消操作");
            if (op == ProtocolOp.Error)
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == ProtocolOp.ManifestEntry)
            {
                output.Add(new FileEntry(
                    frame.GetProperty("p").GetString()!,
                    frame.GetProperty("s").GetInt64(),
                    frame.GetProperty("t").GetInt64()));
            }
        }
    }
}