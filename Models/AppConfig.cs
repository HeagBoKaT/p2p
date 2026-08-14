namespace p2p.Models;

public class AppConfig
{
    public int RetentionDays { get; set; } = 7;
    public int TcpPort { get; set; } = 45454;
    public int DiscoveryPort { get; set; } = 45455;
}
