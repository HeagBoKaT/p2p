namespace p2p.Models;

public class Peer
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SigningPublicKey { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int TcpPort { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}
