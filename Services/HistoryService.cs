using p2p.Models;

namespace p2p.Services;

public class HistoryService
{
    private readonly StorageService _storage;

    public HistoryService(StorageService storage)
    {
        _storage = storage;
    }

    public void PruneExpired(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        foreach (var peerId in _storage.ListHistoryPeerIds())
        {
            var messages = _storage.LoadHistory(peerId);
            var kept = messages.Where(m => m.TimestampUtc >= cutoff).ToList();

            if (kept.Count != messages.Count)
                _storage.SaveHistory(peerId, kept);
        }
    }

    /// <summary>Снимает пометку «ожидает отправки» в сохранённой истории после успешной передачи.</summary>
    public void MarkSent(string peerId, string messageId)
    {
        var messages = _storage.LoadHistory(peerId);
        var stored = messages.FirstOrDefault(m => m.Id == messageId);
        if (stored is null || !stored.Pending)
            return;

        stored.Pending = false;
        _storage.SaveHistory(peerId, messages);
    }

    public void AppendMessage(string peerId, Message message, int maxMessages = 5000)
    {
        var messages = _storage.LoadHistory(peerId);
        messages.Add(message);
        if (messages.Count > maxMessages)
            messages = messages.Skip(messages.Count - maxMessages).ToList();
        _storage.SaveHistory(peerId, messages);
    }
}
