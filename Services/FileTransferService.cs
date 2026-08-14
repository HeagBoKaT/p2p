using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using p2p.Models;

namespace p2p.Services;

public class FileTransferService : IDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly ConnectionService _connections;
    private readonly Dictionary<string, FileTransfer> _transfers = new();
    private readonly Dictionary<string, FileStream> _streams = new();
    private readonly Dictionary<string, (DateTime Time, long Bytes)> _speedSamples = new();
    private readonly object _lock = new();

    public ObservableCollection<FileTransfer> Transfers { get; } = new();

    public event Action<FileTransfer>? IncomingRequest;
    public event Action<FileTransfer>? StateChanged;

    public FileTransferService(ConnectionService connections)
    {
        _connections = connections;
        connections.FileRequestReceived += OnFileRequest;
        connections.FileAccepted += OnFileAccepted;
        connections.FileDeclined += OnFileDeclined;
        connections.FileCanceled += OnFileCanceled;
        connections.FileCompleted += OnFileCompleted;
        connections.FileChunkReceived += OnFileChunk;
    }

    public async Task SendFileAsync(string peerId, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var size = new FileInfo(filePath).Length;
        var sha = await Task.Run(() => CryptoService.ComputeSha256Hex(filePath));

        var transfer = new FileTransfer
        {
            PeerId = peerId,
            FileName = fileName,
            Size = size,
            Sha256 = sha,
            IsSender = true,
            SourcePath = filePath,
            Status = FileTransferStatus.Requested
        };

        AddTransfer(transfer);
        await _connections.SendFileRequestAsync(peerId, transfer);
    }

    public void AcceptTransfer(string transferId, string savePath)
    {
        var transfer = GetTransfer(transferId);
        if (transfer is null)
            return;

        transfer.SavePath = savePath;
        transfer.Status = FileTransferStatus.Accepted;
        UpdateTransfer(transfer);

        FileStream stream;
        try
        {
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            stream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
            lock (_lock) _streams[transferId] = stream;
        }
        catch
        {
            transfer.Status = FileTransferStatus.Failed;
            UpdateTransfer(transfer);
            return;
        }

        _ = _connections.SendFileAcceptAsync(transfer.PeerId, transferId);

        if (transfer.Size == 0)
            FinalizeReceive(transfer, stream);
    }

    public void DeclineTransfer(string transferId)
    {
        var transfer = GetTransfer(transferId);
        if (transfer is null)
            return;

        transfer.Status = FileTransferStatus.Declined;
        UpdateTransfer(transfer);
        _ = _connections.SendFileDeclineAsync(transfer.PeerId, transferId);
        RemoveTransfer(transferId);
    }

    public void CancelTransfer(string transferId)
    {
        var transfer = GetTransfer(transferId);
        if (transfer is null)
            return;

        transfer.Status = FileTransferStatus.Canceled;
        UpdateTransfer(transfer);
        _ = _connections.SendFileCancelAsync(transfer.PeerId, transferId);
        Cleanup(transferId);
        RemoveTransfer(transferId);
    }

    private void OnFileRequest(FileTransfer transfer)
    {
        transfer.Status = FileTransferStatus.Requested;
        AddTransfer(transfer);
        IncomingRequest?.Invoke(transfer);
    }

    private void OnFileAccepted(FileTransfer info)
    {
        var transfer = GetTransfer(info.TransferId);
        if (transfer is null || !transfer.IsSender)
            return;

        transfer.Status = FileTransferStatus.InProgress;
        UpdateTransfer(transfer);
        _ = Task.Run(() => StreamFileAsync(transfer));
    }

    private async Task StreamFileAsync(FileTransfer transfer)
    {
        try
        {
            using var fs = new FileStream(transfer.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[ChunkSize];
            int index = 0;
            long sent = 0;
            int read;
            while ((read = await fs.ReadAsync(buffer)) > 0)
            {
                var chunk = buffer.AsMemory(0, read).ToArray();
                await _connections.SendFileChunkAsync(transfer.PeerId, transfer.TransferId, index++, chunk);
                sent += read;
                transfer.Progress = transfer.Size > 0 ? (double)sent / transfer.Size : 1;
                UpdateSpeed(transfer, sent);
                UpdateTransfer(transfer);
            }
        }
        catch
        {
            transfer.Status = FileTransferStatus.Failed;
            UpdateTransfer(transfer);
        }
    }

    private void OnFileChunk((string TransferId, int Index, byte[] Data) chunk)
    {
        var transfer = GetTransfer(chunk.TransferId);
        if (transfer is null || transfer.IsSender)
            return;

        FileStream? stream;
        lock (_lock) _streams.TryGetValue(chunk.TransferId, out stream);
        if (stream is null)
            return;

        try
        {
            stream.Write(chunk.Data, 0, chunk.Data.Length);
        }
        catch
        {
            transfer.Status = FileTransferStatus.Failed;
            UpdateTransfer(transfer);
            Cleanup(chunk.TransferId);
            return;
        }

        transfer.Progress = transfer.Size > 0 ? (double)stream.Position / transfer.Size : 1;
        if (transfer.Progress > 1)
            transfer.Progress = 1;
        UpdateSpeed(transfer, stream.Position);
        UpdateTransfer(transfer);

        if (stream.Position >= transfer.Size)
            FinalizeReceive(transfer, stream);
    }

    private void FinalizeReceive(FileTransfer transfer, FileStream stream)
    {
        var path = transfer.SavePath;
        stream.Flush();
        stream.Dispose();
        lock (_lock) _streams.Remove(transfer.TransferId);

        bool ok;
        try
        {
            ok = CryptoService.ComputeSha256Hex(path) == transfer.Sha256;
        }
        catch
        {
            ok = false;
        }

        transfer.Status = ok ? FileTransferStatus.Completed : FileTransferStatus.Failed;
        UpdateTransfer(transfer);
        _ = _connections.SendFileCompleteAsync(transfer.PeerId, transfer.TransferId, ok);
        RemoveTransfer(transfer.TransferId);
    }

    private void OnFileCompleted(FileTransfer info)
    {
        var transfer = GetTransfer(info.TransferId);
        if (transfer is null || !transfer.IsSender)
            return;

        transfer.Status = info.Status;
        UpdateTransfer(transfer);
        RemoveTransfer(info.TransferId);
    }

    private void OnFileDeclined(FileTransfer info)
    {
        var transfer = GetTransfer(info.TransferId);
        if (transfer is null)
            return;

        transfer.Status = FileTransferStatus.Declined;
        UpdateTransfer(transfer);
        RemoveTransfer(info.TransferId);
    }

    private void OnFileCanceled(FileTransfer info)
    {
        var transfer = GetTransfer(info.TransferId);
        if (transfer is null)
            return;

        transfer.Status = FileTransferStatus.Canceled;
        UpdateTransfer(transfer);
        Cleanup(info.TransferId);
        RemoveTransfer(info.TransferId);
    }

    /// <summary>Скользящая оценка скорости: обновляется не чаще раза в ~0.2с, чтобы не дёргаться
    /// на каждом 64-КБ чанке, но и не отставать заметно от реального прогресса.</summary>
    private void UpdateSpeed(FileTransfer transfer, long totalBytesTransferred)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_speedSamples.TryGetValue(transfer.TransferId, out var prev))
            {
                var elapsed = (now - prev.Time).TotalSeconds;
                if (elapsed < 0.2)
                    return;

                var delta = totalBytesTransferred - prev.Bytes;
                transfer.BytesPerSecond = delta > 0 ? delta / elapsed : 0;
            }

            _speedSamples[transfer.TransferId] = (now, totalBytesTransferred);
        }
    }

    private void Cleanup(string transferId)
    {
        lock (_lock)
        {
            if (_streams.TryGetValue(transferId, out var stream))
            {
                stream.Dispose();
                _streams.Remove(transferId);
            }
            _speedSamples.Remove(transferId);
        }
    }

    private void AddTransfer(FileTransfer transfer)
    {
        lock (_lock) _transfers[transfer.TransferId] = transfer;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!Transfers.Contains(transfer))
                Transfers.Add(transfer);
        });
    }

    private FileTransfer? GetTransfer(string transferId)
    {
        lock (_lock) return _transfers.TryGetValue(transferId, out var t) ? t : null;
    }

    private void UpdateTransfer(FileTransfer transfer)
    {
        StateChanged?.Invoke(transfer);
    }

    private void RemoveTransfer(string transferId)
    {
        lock (_lock)
        {
            _transfers.Remove(transferId);
            _speedSamples.Remove(transferId);
        }
        var item = Transfers.FirstOrDefault(t => t.TransferId == transferId);
        if (item is not null)
            Application.Current.Dispatcher.Invoke(() => Transfers.Remove(item));
    }

    public void Dispose()
    {
        _connections.FileRequestReceived -= OnFileRequest;
        _connections.FileAccepted -= OnFileAccepted;
        _connections.FileDeclined -= OnFileDeclined;
        _connections.FileCanceled -= OnFileCanceled;
        _connections.FileCompleted -= OnFileCompleted;
        _connections.FileChunkReceived -= OnFileChunk;

        lock (_lock)
        {
            foreach (var stream in _streams.Values)
                stream.Dispose();
            _streams.Clear();
            _speedSamples.Clear();
        }
    }
}
