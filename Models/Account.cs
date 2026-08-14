namespace p2p.Models;

public class Account
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SigningPublicKey { get; set; } = "";
    public string SigningPrivateKeyEncrypted { get; set; } = "";
}
