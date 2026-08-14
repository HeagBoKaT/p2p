using System.Net.Sockets;

namespace p2p.Services;

public class Connection : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TcpClient Client { get; }
    public NetworkStream Stream { get; }
    public string PeerId { get; set; } = "";
    public string PeerDisplayName { get; set; } = "";
    public string PeerSigningPublicKey { get; set; } = "";
    public bool IsOutbound { get; set; }
    public byte[] SendKey { get; set; } = Array.Empty<byte>();
    public byte[] RecvKey { get; set; } = Array.Empty<byte>();

    public Connection(TcpClient client)
    {
        Client = client;
        Stream = client.GetStream();
        client.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
    }

    public async Task SendFrameAsync(FrameType type, ReadOnlyMemory<byte> body)
    {
        var payload = new byte[1 + body.Length];
        payload[0] = (byte)type;
        body.CopyTo(payload.AsMemory(1));

        var encrypted = CryptoService.Encrypt(payload, SendKey);
        var lenBytes = BitConverter.GetBytes(encrypted.Length);

        await _sendLock.WaitAsync();
        try
        {
            await Stream.WriteAsync(lenBytes);
            await Stream.WriteAsync(encrypted);
            await Stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { Client.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
