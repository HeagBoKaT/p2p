using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using p2p.Models;

namespace p2p.Services;

public class ConnectionService : IDisposable
{
    private const int MaxFrameSize = 128 * 1024 * 1024;

    private readonly Account _account;
    private readonly string _signingPrivateKey;
    private readonly AppConfig _config;
    private readonly StorageService _storage;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Connection> _connections = new();
    private readonly HashSet<string> _connecting = new();
    private readonly object _lock = new();

    public event Action<Peer>? PeerConnected;
    public event Action<string>? PeerDisconnected;
    public event Action<string, Message>? MessageReceived;
    public event Action<FileTransfer>? FileRequestReceived;
    public event Action<FileTransfer>? FileAccepted;
    public event Action<FileTransfer>? FileDeclined;
    public event Action<FileTransfer>? FileCanceled;
    public event Action<FileTransfer>? FileCompleted;
    public event Action<(string TransferId, int Index, byte[] Data)>? FileChunkReceived;

    public string UserId => _account.UserId;

    public ConnectionService(Account account, string signingPrivateKey, AppConfig config, StorageService storage)
    {
        _account = account;
        _signingPrivateKey = signingPrivateKey;
        _config = config;
        _storage = storage;
        _listener = new TcpListener(IPAddress.Any, config.TcpPort);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(KeepAliveLoopAsync);
    }

    public bool IsConnected(string peerId)
    {
        lock (_lock) return _connections.ContainsKey(peerId);
    }

    public async Task EnsureConnectionAsync(Peer peer)
    {
        if (peer.UserId == _account.UserId)
            return;

        lock (_lock)
        {
            if (_connections.ContainsKey(peer.UserId))
                return;
            if (!_connecting.Add(peer.UserId))
                return;
        }

        try
        {
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(peer.IpAddress, peer.TcpPort, timeout.Token);
                await EstablishOutboundAsync(client);
            }
            catch
            {
                client?.Dispose();
            }
        }
        finally
        {
            lock (_lock) _connecting.Remove(peer.UserId);
        }
    }

    public async Task<Peer?> ConnectToEndpointAsync(string ip, int port)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(ip, port, timeout.Token);
            return await EstablishOutboundAsync(client);
        }
        catch
        {
            client?.Dispose();
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await EstablishInboundAsync(client);
                }
                catch
                {
                    client.Dispose();
                }
            });
        }
    }

    private async Task<Peer?> EstablishOutboundAsync(TcpClient client)
    {
        var conn = new Connection(client);
        var ephemeral = CryptoService.GenerateEphemeral(out var ephemeralPub);
        var nonce = RandomNumberGenerator.GetBytes(16);

        await SendHelloAsync(conn, BuildHello(ephemeralPub, nonce));

        var peerHello = await ReceiveHelloAsync(conn);
        var (sendKey, recvKey) = DeriveKeys(ephemeral, nonce, peerHello);

        if (!VerifyPeer(peerHello))
            throw new InvalidOperationException("Peer identity verification failed.");

        conn.PeerId = peerHello.UserId;
        conn.PeerDisplayName = peerHello.DisplayName;
        conn.PeerSigningPublicKey = peerHello.SigningPublicKey;
        conn.SendKey = sendKey;
        conn.RecvKey = recvKey;
        conn.IsOutbound = true;

        var peer = Register(conn);
        if (peer is null)
        {
            conn.Dispose();
            return null;
        }

        StartReadLoop(conn);
        return peer;
    }

    private async Task EstablishInboundAsync(TcpClient client)
    {
        var conn = new Connection(client);

        var peerHello = await ReceiveHelloAsync(conn);

        var ephemeral = CryptoService.GenerateEphemeral(out var ephemeralPub);
        var nonce = RandomNumberGenerator.GetBytes(16);

        await SendHelloAsync(conn, BuildHello(ephemeralPub, nonce));

        var (sendKey, recvKey) = DeriveKeys(ephemeral, nonce, peerHello);

        if (!VerifyPeer(peerHello))
            throw new InvalidOperationException("Peer identity verification failed.");

        conn.PeerId = peerHello.UserId;
        conn.PeerDisplayName = peerHello.DisplayName;
        conn.PeerSigningPublicKey = peerHello.SigningPublicKey;
        conn.SendKey = sendKey;
        conn.RecvKey = recvKey;
        conn.IsOutbound = false;

        var peer = Register(conn);
        if (peer is null)
        {
            conn.Dispose();
            return;
        }

        StartReadLoop(conn);
    }

    private HelloFrame BuildHello(string ephemeralPublicKey, byte[] nonce)
    {
        var ephemeralBytes = Convert.FromBase64String(ephemeralPublicKey);
        var sigData = new byte[ephemeralBytes.Length + nonce.Length];
        Buffer.BlockCopy(ephemeralBytes, 0, sigData, 0, ephemeralBytes.Length);
        Buffer.BlockCopy(nonce, 0, sigData, ephemeralBytes.Length, nonce.Length);

        return new HelloFrame
        {
            UserId = _account.UserId,
            DisplayName = _account.DisplayName,
            SigningPublicKey = _account.SigningPublicKey,
            Nonce = Convert.ToBase64String(nonce),
            EphemeralPublicKey = ephemeralPublicKey,
            Signature = CryptoService.Sign(_signingPrivateKey, sigData)
        };
    }

    private (byte[] sendKey, byte[] recvKey) DeriveKeys(ECDiffieHellman ephemeral, byte[] myNonce, HelloFrame peerHello)
    {
        var shared = CryptoService.ComputeSharedSecret(ephemeral, peerHello.EphemeralPublicKey);
        var peerNonce = Convert.FromBase64String(peerHello.Nonce);

        var saltSend = new byte[myNonce.Length + peerNonce.Length];
        Buffer.BlockCopy(myNonce, 0, saltSend, 0, myNonce.Length);
        Buffer.BlockCopy(peerNonce, 0, saltSend, myNonce.Length, peerNonce.Length);

        var saltRecv = new byte[myNonce.Length + peerNonce.Length];
        Buffer.BlockCopy(peerNonce, 0, saltRecv, 0, peerNonce.Length);
        Buffer.BlockCopy(myNonce, 0, saltRecv, peerNonce.Length, myNonce.Length);

        var sendKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, saltSend, Encoding.UTF8.GetBytes("p2p-session-v1"));
        var recvKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, saltRecv, Encoding.UTF8.GetBytes("p2p-session-v1"));
        return (sendKey, recvKey);
    }

    private bool VerifyPeer(HelloFrame hello)
    {
        var ephemeralBytes = Convert.FromBase64String(hello.EphemeralPublicKey);
        var nonceBytes = Convert.FromBase64String(hello.Nonce);
        var sigData = new byte[ephemeralBytes.Length + nonceBytes.Length];
        Buffer.BlockCopy(ephemeralBytes, 0, sigData, 0, ephemeralBytes.Length);
        Buffer.BlockCopy(nonceBytes, 0, sigData, ephemeralBytes.Length, nonceBytes.Length);

        if (!CryptoService.Verify(hello.SigningPublicKey, sigData, hello.Signature))
            return false;

        PinPeerKey(hello);
        return true;
    }

    private void PinPeerKey(HelloFrame hello)
    {
        try
        {
            var peers = _storage.LoadPeers();
            if (peers.TryGetValue(hello.UserId, out var existing))
            {
                existing.DisplayName = hello.DisplayName;
                existing.SigningPublicKey = hello.SigningPublicKey;
                existing.LastSeenUtc = DateTime.UtcNow;
            }
            else
            {
                peers[hello.UserId] = new Peer
                {
                    UserId = hello.UserId,
                    DisplayName = hello.DisplayName,
                    SigningPublicKey = hello.SigningPublicKey,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            _storage.SavePeers(peers);
        }
        catch
        {
        }
    }

    private Peer? Register(Connection conn)
    {
        Connection? toDispose = null;
        bool keepNew;

        lock (_lock)
        {
            if (_connections.TryGetValue(conn.PeerId, out var existing))
            {
                var existingInitiator = existing.IsOutbound ? _account.UserId : existing.PeerId;
                var newInitiator = conn.IsOutbound ? _account.UserId : conn.PeerId;

                if (string.CompareOrdinal(newInitiator, existingInitiator) < 0)
                {
                    _connections[conn.PeerId] = conn;
                    toDispose = existing;
                    keepNew = true;
                }
                else
                {
                    keepNew = false;
                }
            }
            else
            {
                _connections[conn.PeerId] = conn;
                keepNew = true;
            }
        }

        toDispose?.Dispose();

        if (!keepNew)
            return null;

        var peer = new Peer
        {
            UserId = conn.PeerId,
            DisplayName = conn.PeerDisplayName,
            SigningPublicKey = conn.PeerSigningPublicKey,
            LastSeenUtc = DateTime.UtcNow
        };

        if (conn.Client.Client.RemoteEndPoint is IPEndPoint remote)
            peer.IpAddress = remote.Address.ToString();

        PeerConnected?.Invoke(peer);
        return peer;
    }

    private void RemoveConnection(Connection conn)
    {
        lock (_lock)
        {
            if (_connections.TryGetValue(conn.PeerId, out var existing) && ReferenceEquals(existing, conn))
                _connections.Remove(conn.PeerId);
        }

        conn.Dispose();
        PeerDisconnected?.Invoke(conn.PeerId);
    }

    private void StartReadLoop(Connection conn)
    {
        _ = Task.Run(() => ReadLoopAsync(conn));
    }

    private async Task ReadLoopAsync(Connection conn)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var payload = await ReadEncryptedFrameAsync(conn);
                if (payload is null)
                    break;

                var type = (FrameType)payload[0];
                var body = payload.AsMemory(1);
                Dispatch(conn, type, body);
            }
        }
        catch
        {
        }
        finally
        {
            RemoveConnection(conn);
        }
    }

    private void Dispatch(Connection conn, FrameType type, ReadOnlyMemory<byte> body)
    {
        switch (type)
        {
            case FrameType.Message:
                HandleMessage(conn, body);
                break;
            case FrameType.FileRequest:
                HandleFileRequest(conn, body);
                break;
            case FrameType.FileAccept:
                HandleFileAccept(conn, body);
                break;
            case FrameType.FileDecline:
                HandleFileDecline(conn, body);
                break;
            case FrameType.FileChunk:
                HandleFileChunk(conn, body);
                break;
            case FrameType.FileComplete:
                HandleFileComplete(conn, body);
                break;
            case FrameType.FileCancel:
                HandleFileCancel(conn, body);
                break;
            case FrameType.Ping:
                break;
        }
    }

    private void HandleMessage(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireMessage>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        var message = new Message
        {
            Id = wire.Id,
            Direction = MessageDirection.Incoming,
            Kind = wire.Kind == "file" ? MessageKind.File : MessageKind.Text,
            Text = wire.Text,
            FileName = wire.FileName,
            FileSize = wire.FileSize,
            FileSha256 = wire.FileSha256,
            TimestampUtc = wire.TimestampUtc
        };

        MessageReceived?.Invoke(conn.PeerId, message);
    }

    private void HandleFileRequest(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireFileRequest>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        var transfer = new FileTransfer
        {
            TransferId = wire.TransferId,
            PeerId = conn.PeerId,
            FileName = wire.FileName,
            Size = wire.Size,
            Sha256 = wire.Sha256,
            IsSender = false
        };

        FileRequestReceived?.Invoke(transfer);
    }

    private void HandleFileAccept(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireFileAccept>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        FileAccepted?.Invoke(new FileTransfer { TransferId = wire.TransferId, PeerId = conn.PeerId });
    }

    private void HandleFileDecline(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireFileDecline>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        FileDeclined?.Invoke(new FileTransfer { TransferId = wire.TransferId, PeerId = conn.PeerId });
    }

    private void HandleFileComplete(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireFileComplete>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        var transfer = new FileTransfer { TransferId = wire.TransferId, PeerId = conn.PeerId };
        transfer.Status = wire.Ok ? FileTransferStatus.Completed : FileTransferStatus.Failed;
        FileCompleted?.Invoke(transfer);
    }

    private void HandleFileCancel(Connection conn, ReadOnlyMemory<byte> body)
    {
        var wire = JsonUtil.Deserialize<WireFileCancel>(Encoding.UTF8.GetString(body.Span));
        if (wire is null)
            return;

        FileCanceled?.Invoke(new FileTransfer { TransferId = wire.TransferId, PeerId = conn.PeerId });
    }

    private void HandleFileChunk(Connection conn, ReadOnlyMemory<byte> body)
    {
        if (body.Length < 20)
            return;

        var transferId = new Guid(body.Span[..16]).ToString("N");
        var index = BitConverter.ToInt32(body.Span[16..20]);
        var data = body[20..].ToArray();

        FileChunkReceived?.Invoke((transferId, index, data));
    }

    private async Task KeepAliveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(15000, _cts.Token);
            }
            catch
            {
                break;
            }

            Connection[] snapshot;
            lock (_lock) snapshot = _connections.Values.ToArray();

            foreach (var conn in snapshot)
            {
                try
                {
                    await conn.SendFrameAsync(FrameType.Ping, Array.Empty<byte>());
                }
                catch
                {
                    RemoveConnection(conn);
                }
            }
        }
    }

    private async Task SendHelloAsync(Connection conn, HelloFrame hello)
    {
        var json = Encoding.UTF8.GetBytes(JsonUtil.Serialize(hello));
        var lenBytes = BitConverter.GetBytes(json.Length);
        await conn.Stream.WriteAsync(lenBytes);
        await conn.Stream.WriteAsync(json);
        await conn.Stream.FlushAsync();
    }

    private async Task<HelloFrame> ReceiveHelloAsync(Connection conn)
    {
        var lenBytes = await ReadExactlyAsync(conn, 4);
        if (lenBytes is null)
            throw new EndOfStreamException();

        var len = BitConverter.ToInt32(lenBytes, 0);
        if (len <= 0 || len > MaxFrameSize)
            throw new InvalidDataException("Bad hello length.");

        var jsonBytes = await ReadExactlyAsync(conn, len);
        if (jsonBytes is null)
            throw new EndOfStreamException();

        return JsonUtil.Deserialize<HelloFrame>(Encoding.UTF8.GetString(jsonBytes))
            ?? throw new InvalidDataException("Bad hello frame.");
    }

    private async Task<byte[]?> ReadEncryptedFrameAsync(Connection conn)
    {
        var lenBytes = await ReadExactlyAsync(conn, 4);
        if (lenBytes is null)
            return null;

        var len = BitConverter.ToInt32(lenBytes, 0);
        if (len <= 0 || len > MaxFrameSize)
            throw new InvalidDataException("Bad frame length.");

        var encrypted = await ReadExactlyAsync(conn, len);
        if (encrypted is null)
            return null;

        return CryptoService.Decrypt(encrypted, conn.RecvKey);
    }

    private static async Task<byte[]?> ReadExactlyAsync(Connection conn, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            var read = await conn.Stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
                return null;
            offset += read;
        }
        return buffer;
    }

    private Connection? GetConnection(string peerId)
    {
        lock (_lock) return _connections.TryGetValue(peerId, out var c) ? c : null;
    }

    /// <summary>Возвращает true, только если кадр действительно ушёл в сокет: очередь отправки
    /// опирается на этот признак, чтобы не терять сообщения при обрыве связи.</summary>
    public async Task<bool> SendTextAsync(string peerId, Message message)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return false;

        var wire = new WireMessage
        {
            Id = message.Id,
            Kind = message.Kind == MessageKind.File ? "file" : "text",
            Text = message.Text,
            FileName = message.FileName,
            FileSize = message.FileSize,
            FileSha256 = message.FileSha256,
            TimestampUtc = message.TimestampUtc
        };

        try
        {
            await SendJsonAsync(conn, FrameType.Message, wire);
            return true;
        }
        catch
        {
            RemoveConnection(conn);
            return false;
        }
    }

    public Task SendFileRequestAsync(string peerId, FileTransfer transfer)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return Task.CompletedTask;

        return SendJsonAsync(conn, FrameType.FileRequest, new WireFileRequest
        {
            TransferId = transfer.TransferId,
            FileName = transfer.FileName,
            Size = transfer.Size,
            Sha256 = transfer.Sha256
        });
    }

    public Task SendFileAcceptAsync(string peerId, string transferId)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return Task.CompletedTask;

        return SendJsonAsync(conn, FrameType.FileAccept, new WireFileAccept { TransferId = transferId });
    }

    public Task SendFileDeclineAsync(string peerId, string transferId)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return Task.CompletedTask;

        return SendJsonAsync(conn, FrameType.FileDecline, new WireFileDecline { TransferId = transferId });
    }

    public Task SendFileCancelAsync(string peerId, string transferId)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return Task.CompletedTask;

        return SendJsonAsync(conn, FrameType.FileCancel, new WireFileCancel { TransferId = transferId });
    }

    public Task SendFileCompleteAsync(string peerId, string transferId, bool ok)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return Task.CompletedTask;

        return SendJsonAsync(conn, FrameType.FileComplete, new WireFileComplete { TransferId = transferId, Ok = ok });
    }

    public async Task SendFileChunkAsync(string peerId, string transferId, int index, byte[] chunk)
    {
        var conn = GetConnection(peerId);
        if (conn is null)
            return;

        var transferIdBytes = new Guid(transferId).ToByteArray();
        var body = new byte[16 + 4 + chunk.Length];
        Buffer.BlockCopy(transferIdBytes, 0, body, 0, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(index), 0, body, 16, 4);
        Buffer.BlockCopy(chunk, 0, body, 20, chunk.Length);

        await conn.SendFrameAsync(FrameType.FileChunk, body);
    }

    private static Task SendJsonAsync(Connection conn, FrameType type, object dto)
    {
        var body = Encoding.UTF8.GetBytes(JsonUtil.Serialize(dto));
        return conn.SendFrameAsync(type, body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }

        Connection[] snapshot;
        lock (_lock)
        {
            snapshot = _connections.Values.ToArray();
            _connections.Clear();
        }

        foreach (var conn in snapshot)
            conn.Dispose();

        _cts.Dispose();
    }
}
