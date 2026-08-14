using System.IO;
using p2p.Models;

namespace p2p.Services;

/// <summary>
/// Очередь отложенной отправки. Всё, что не ушло собеседнику (он офлайн или связь оборвалась),
/// складывается сюда, переживает перезапуск приложения и уходит автоматически в фоне,
/// как только с собеседником установлено соединение.
/// </summary>
public class OutboxService
{
    private readonly StorageService _storage;
    private readonly ConnectionService _connections;
    private readonly FileTransferService _fileTransfers;
    private readonly Dictionary<string, List<OutboxItem>> _queues;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>Элемент очереди успешно передан собеседнику.</summary>
    public event Action<string, OutboxItem>? ItemSent;

    /// <summary>Элемент выброшен из очереди без отправки (например, файл удалён с диска).</summary>
    public event Action<string, OutboxItem, string>? ItemDropped;

    public OutboxService(StorageService storage, ConnectionService connections, FileTransferService fileTransfers)
    {
        _storage = storage;
        _connections = connections;
        _fileTransfers = fileTransfers;
        _queues = storage.LoadOutbox();

        // Появление соединения — единственный надёжный триггер: он срабатывает и для входящих
        // подключений, и для исходящих, которые поднимает обнаружение в сети.
        _connections.PeerConnected += peer => _ = FlushAsync(peer.UserId);
    }

    public int PendingCount(string peerId)
    {
        lock (_lock)
            return _queues.TryGetValue(peerId, out var queue) ? queue.Count : 0;
    }

    /// <summary>Стоит ли сообщение из истории всё ещё в очереди (для восстановления пометки после запуска).</summary>
    public bool ContainsMessage(string peerId, string messageId)
    {
        lock (_lock)
            return _queues.TryGetValue(peerId, out var queue) &&
                   queue.Any(i => i.Kind == OutboxKind.Text && i.Message?.Id == messageId);
    }

    public void QueueText(string peerId, Message message)
    {
        Add(peerId, new OutboxItem { Kind = OutboxKind.Text, Message = message });
    }

    public void QueueFile(string peerId, string filePath)
    {
        Add(peerId, new OutboxItem { Kind = OutboxKind.File, FilePath = filePath });
    }

    public async Task FlushAllAsync()
    {
        string[] peerIds;
        lock (_lock) peerIds = _queues.Keys.ToArray();

        foreach (var peerId in peerIds)
            await FlushAsync(peerId);
    }

    /// <summary>Пытается отправить очередь собеседника по порядку. Останавливается на первой неудаче,
    /// чтобы не нарушить порядок сообщений — остаток уйдёт при следующем подключении.</summary>
    public async Task FlushAsync(string peerId)
    {
        if (PendingCount(peerId) == 0)
            return;

        await _flushGate.WaitAsync();
        try
        {
            while (true)
            {
                if (!_connections.IsConnected(peerId))
                    return;

                OutboxItem item;
                lock (_lock)
                {
                    if (!_queues.TryGetValue(peerId, out var queue) || queue.Count == 0)
                        return;
                    item = queue[0];
                }

                if (item.Kind == OutboxKind.File)
                {
                    if (!File.Exists(item.FilePath))
                    {
                        Remove(peerId, item);
                        ItemDropped?.Invoke(peerId, item, "файл больше не найден на диске");
                        continue;
                    }

                    try
                    {
                        await _fileTransfers.SendFileAsync(peerId, item.FilePath);
                    }
                    catch (Exception ex)
                    {
                        Remove(peerId, item);
                        ItemDropped?.Invoke(peerId, item, ex.Message);
                        continue;
                    }
                }
                else
                {
                    if (item.Message is null)
                    {
                        Remove(peerId, item);
                        continue;
                    }

                    if (!await _connections.SendTextAsync(peerId, item.Message))
                        return;
                }

                Remove(peerId, item);
                ItemSent?.Invoke(peerId, item);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void Add(string peerId, OutboxItem item)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(peerId, out var queue))
                _queues[peerId] = queue = new List<OutboxItem>();
            queue.Add(item);
            Persist();
        }
    }

    private void Remove(string peerId, OutboxItem item)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(peerId, out var queue))
                return;

            queue.Remove(item);
            if (queue.Count == 0)
                _queues.Remove(peerId);

            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            _storage.SaveOutbox(_queues);
        }
        catch
        {
            // Потеря очереди на диске не должна ломать отправку в текущем сеансе.
        }
    }
}
