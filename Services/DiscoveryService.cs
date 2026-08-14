using System.Net;
using System.Net.Sockets;
using System.Text;
using p2p.Models;

namespace p2p.Services;

public class DiscoveryService : IDisposable
{
    private readonly Account _account;
    private readonly AppConfig _config;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Peer> _discovered = new();
    private readonly object _lock = new();

    public event Action<Peer>? PeerDiscovered;

    public DiscoveryService(Account account, AppConfig config)
    {
        _account = account;
        _config = config;

        _udp = new UdpClient { EnableBroadcast = true, ExclusiveAddressUse = false };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, config.DiscoveryPort));
    }

    public void Start()
    {
        _ = Task.Run(BroadcastLoopAsync);
        _ = Task.Run(ListenLoopAsync);
    }

    public IReadOnlyList<Peer> GetDiscovered()
    {
        lock (_lock) return _discovered.Values.ToList();
    }

    private async Task BroadcastLoopAsync()
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, _config.DiscoveryPort);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var beacon = new DiscoveryBeacon
                {
                    UserId = _account.UserId,
                    DisplayName = _account.DisplayName,
                    SigningPublicKey = _account.SigningPublicKey,
                    TcpPort = _config.TcpPort
                };
                var bytes = Encoding.UTF8.GetBytes(JsonUtil.Serialize(beacon));
                await _udp.SendAsync(bytes, endpoint);
            }
            catch
            {
            }

            try
            {
                await Task.Delay(4000, _cts.Token);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var beacon = JsonUtil.Deserialize<DiscoveryBeacon>(json);

                if (beacon is null || beacon.Magic != DiscoveryBeacon.ProtocolMagic)
                    continue;
                if (beacon.UserId == _account.UserId)
                    continue;

                var peer = new Peer
                {
                    UserId = beacon.UserId,
                    DisplayName = beacon.DisplayName,
                    SigningPublicKey = beacon.SigningPublicKey,
                    IpAddress = result.RemoteEndPoint.Address.ToString(),
                    TcpPort = beacon.TcpPort,
                    LastSeenUtc = DateTime.UtcNow
                };

                lock (_lock) _discovered[peer.UserId] = peer;
                PeerDiscovered?.Invoke(peer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
