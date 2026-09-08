using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace LanFileSync;

public sealed class PeerConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _ns;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PeerConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _ns = tcp.GetStream();
    }

    public async Task SendJsonAsync(object obj, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(obj);
        byte[] len = BitConverter.GetBytes(json.Length);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _ns.WriteAsync(len, ct);
            await _ns.WriteAsync(json, ct);
            await _ns.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendRawAsync(byte[] buffer, int count, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _ns.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<JsonElement?> RecvJsonAsync(CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        await ReadExactAsync(lenBuf.AsMemory(), ct);
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > Constants.MaxFrameSize)
            throw new IOException("无效的帧长度: " + len);

        byte[] body = new byte[len];
        await ReadExactAsync(body.AsMemory(), ct);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    public async Task ReadRawAsync(byte[] buffer, int count, CancellationToken ct)
        => await ReadExactAsync(buffer.AsMemory(0, count), ct);

    private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await _ns.ReadAsync(buffer.Slice(offset), ct);
            if (n <= 0)
                throw new EndOfStreamException("连接已断开");
            offset += n;
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _ns.Dispose();
        _tcp.Dispose();
    }
}