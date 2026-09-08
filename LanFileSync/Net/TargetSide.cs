using System.IO;

namespace LanFileSync;

public static class TargetSide
{
    public static async Task ReceiveManifestAsync(PeerConnection peer, CancellationToken ct, List<FileEntry> output)
    {
        while (true)
        {
            var frame = await peer.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");

            string op = frame.GetProperty("op").GetString()!;
            if (op == "mend")
                return;
            if (op == "err")
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == "f")
            {
                output.Add(new FileEntry(
                    frame.GetProperty("p").GetString()!,
                    frame.GetProperty("s").GetInt64(),
                    frame.GetProperty("t").GetInt64()));
            }
        }
    }
}