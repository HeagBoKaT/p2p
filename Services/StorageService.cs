using System.IO;
using p2p.Models;

namespace p2p.Services;

public class StorageService
{
    private readonly object _lock = new();

    public string BaseDir { get; }
    public string ConfigPath => Path.Combine(BaseDir, "config.json");
    public string AccountPath => Path.Combine(BaseDir, "account.json");
    public string PeersPath => Path.Combine(BaseDir, "peers.json");
    public string OutboxPath => Path.Combine(BaseDir, "outbox.json");
    public string HistoryDir => Path.Combine(BaseDir, "history");

    public StorageService(string? baseDir = null)
    {
        BaseDir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "p2p");
        Directory.CreateDirectory(HistoryDir);
    }

    public AppConfig LoadConfig()
    {
        return ReadJson<AppConfig>(ConfigPath) ?? new AppConfig();
    }

    public void SaveConfig(AppConfig config)
    {
        WriteJson(ConfigPath, config);
    }

    public Account? LoadAccount()
    {
        return ReadJson<Account>(AccountPath);
    }

    public void SaveAccount(Account account)
    {
        WriteJson(AccountPath, account);
    }

    public Dictionary<string, Peer> LoadPeers()
    {
        return ReadJson<Dictionary<string, Peer>>(PeersPath) ?? new();
    }

    public void SavePeers(Dictionary<string, Peer> peers)
    {
        WriteJson(PeersPath, peers);
    }

    public Dictionary<string, List<OutboxItem>> LoadOutbox()
    {
        return ReadJson<Dictionary<string, List<OutboxItem>>>(OutboxPath) ?? new();
    }

    public void SaveOutbox(Dictionary<string, List<OutboxItem>> outbox)
    {
        WriteJson(OutboxPath, outbox);
    }

    public List<Message> LoadHistory(string peerId)
    {
        return ReadJson<List<Message>>(HistoryPath(peerId)) ?? new();
    }

    public void SaveHistory(string peerId, List<Message> messages)
    {
        WriteJson(HistoryPath(peerId), messages);
    }

    public IEnumerable<string> ListHistoryPeerIds()
    {
        if (!Directory.Exists(HistoryDir))
            return Array.Empty<string>();

        return Directory.GetFiles(HistoryDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));
    }

    private string HistoryPath(string peerId) => Path.Combine(HistoryDir, $"{peerId}.json");

    private T? ReadJson<T>(string path)
    {
        lock (_lock)
        {
            if (!File.Exists(path))
                return default;

            try
            {
                var json = File.ReadAllText(path);
                return JsonUtil.Deserialize<T>(json);
            }
            catch
            {
                return default;
            }
        }
    }

    private void WriteJson<T>(string path, T value)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
