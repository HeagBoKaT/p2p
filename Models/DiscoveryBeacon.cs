namespace p2p.Models;

public class DiscoveryBeacon
{
    public const int ProtocolMagic = 0x50325032;

    public int Magic { get; set; } = ProtocolMagic;
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SigningPublicKey { get; set; } = "";
    public int TcpPort { get; set; }
}
