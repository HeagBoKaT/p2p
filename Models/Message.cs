using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace p2p.Models;

public enum MessageDirection
{
    Incoming,
    Outgoing
}

public enum MessageKind
{
    Text,
    File,

    /// <summary>Служебная запись в ленте (например, «файл отклонён»): не от пользователя,
    /// показывается по центру без обычного пузыря отправителя/получателя.</summary>
    System
}

public partial class Message : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MessageDirection Direction { get; set; }
    public MessageKind Kind { get; set; } = MessageKind.Text;
    public string Text { get; set; } = "";

    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FileSha256 { get; set; } = "";
    public string SavedPath { get; set; } = "";

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Исходящее сообщение лежит в очереди отправки: собеседник был недоступен.
    /// Сохраняется в историю, чтобы очередь пережила перезапуск приложения.</summary>
    [ObservableProperty]
    private bool pending;

    [JsonIgnore]
    public bool IsOutgoing => Direction == MessageDirection.Outgoing;

    [JsonIgnore]
    public bool IsFile => Kind == MessageKind.File;

    [JsonIgnore]
    public bool IsSystem => Kind == MessageKind.System;

    [JsonIgnore]
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm");

    [JsonIgnore]
    public string FileSizeText => FormatBytes(FileSize);

    [JsonIgnore]
    public string DisplayText =>
        Kind == MessageKind.File
            ? $"Файл: {FileName} ({FormatBytes(FileSize)})"
            : Text;

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} МБ";
        return $"{bytes / (1024.0 * 1024 * 1024):0.#} ГБ";
    }
}
