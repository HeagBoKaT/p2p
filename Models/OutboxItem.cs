namespace p2p.Models;

public enum OutboxKind
{
    Text,
    File
}

/// <summary>Элемент очереди отложенной отправки: то, что не удалось передать, пока собеседник был офлайн.</summary>
public class OutboxItem
{
    public OutboxKind Kind { get; set; } = OutboxKind.Text;

    /// <summary>Само сообщение для <see cref="OutboxKind.Text"/>; для файла — null.</summary>
    public Message? Message { get; set; }

    /// <summary>Путь к файлу на диске для <see cref="OutboxKind.File"/>.</summary>
    public string FilePath { get; set; } = "";

    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;
}
