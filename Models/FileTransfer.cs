using CommunityToolkit.Mvvm.ComponentModel;

namespace p2p.Models;

public enum FileTransferStatus
{
    Requested,
    Accepted,
    Declined,
    InProgress,
    Completed,
    Failed,
    Canceled
}

public partial class FileTransfer : ObservableObject
{
    public string TransferId { get; set; } = Guid.NewGuid().ToString("N");
    public string PeerId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public bool IsSender { get; set; }
    public string SourcePath { get; set; } = "";
    public string SavePath { get; set; } = "";

    [ObservableProperty]
    private FileTransferStatus status = FileTransferStatus.Requested;

    [ObservableProperty]
    private double progress;

    /// <summary>Текущая скорость передачи, байт/с (сглаженная по короткому окну, см. FileTransferService).</summary>
    [ObservableProperty]
    private double bytesPerSecond;

    public string SpeedText => BytesPerSecond > 0 ? $"{Message.FormatBytes((long)BytesPerSecond)}/с" : "";

    partial void OnBytesPerSecondChanged(double value) => OnPropertyChanged(nameof(SpeedText));
}
