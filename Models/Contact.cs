using CommunityToolkit.Mvvm.ComponentModel;

namespace p2p.Models;

public partial class Contact : ObservableObject
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SigningPublicKey { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int TcpPort { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastMessageUtc { get; set; }

    [ObservableProperty]
    private bool isOnline;

    [ObservableProperty]
    private int unreadCount;

    [ObservableProperty]
    private string lastMessagePreview = "";

    [ObservableProperty]
    private string statusText = "";

    /// <summary>true — с этим человеком уже есть история/подтверждённое соединение, он в «Контактах».
    /// false — просто обнаружен по сети (UDP-маячок), но переписки с ним ещё не было: показывается
    /// в отдельном списке «Устройства в сети», пока пользователь сам не начнёт диалог.</summary>
    [ObservableProperty]
    private bool isKnownContact;
}
