using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using p2p.Models;
using p2p.Services;
using p2p.Views;

namespace p2p.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly Account _account;
    private readonly AppConfig _config;
    private readonly StorageService _storage;
    private readonly HistoryService _history;
    private readonly DiscoveryService _discovery;
    private readonly ConnectionService _connections;
    private readonly FileTransferService _fileTransfers;
    private readonly OutboxService _outbox;
    private readonly UpdateService _updates;
    private UpdateInfo? _pendingUpdate;
    private readonly Dictionary<string, Contact> _contacts = new();
    private readonly HashSet<string> _historyRecorded = new();
    private readonly Dictionary<string, string> _drafts = new();
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<Contact> Contacts { get; } = new();

    /// <summary>Устройства, обнаруженные в сети, с которыми ещё не было переписки/подключения.
    /// Как только пользователь начинает диалог, устройство становится обычным контактом
    /// и переезжает в <see cref="Contacts"/> (см. OpenDeviceCommand).</summary>
    public ObservableCollection<Contact> Devices { get; } = new();
    public ObservableCollection<Message> Messages { get; } = new();
    public ObservableCollection<FileTransfer> Transfers => _fileTransfers.Transfers;

    [ObservableProperty]
    private Contact? selectedContact;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private string messageInput = "";

    [ObservableProperty]
    private string myName = "";

    [ObservableProperty]
    private string myId = "";

    [ObservableProperty]
    private string statusMessage = "";

    [ObservableProperty]
    private bool updateAvailable;

    [ObservableProperty]
    private string updateBannerText = "";

    [ObservableProperty]
    private bool updateInProgress;

    [ObservableProperty]
    private double updateProgress;

    public string BannerDisplayText => UpdateInProgress
        ? $"Скачивание обновления… {UpdateProgress:P0}"
        : UpdateBannerText;

    partial void OnUpdateProgressChanged(double value) => OnPropertyChanged(nameof(BannerDisplayText));
    partial void OnUpdateInProgressChanged(bool value) => OnPropertyChanged(nameof(BannerDisplayText));
    partial void OnUpdateBannerTextChanged(string value) => OnPropertyChanged(nameof(BannerDisplayText));

    public string MyShortId => _account.UserId.Length <= 8 ? _account.UserId : _account.UserId[..8];

    /// <summary>Lets the view report whether the main window currently has focus, without the VM depending on it.</summary>
    public Func<bool>? IsWindowActive { get; set; }

    /// <summary>Raised when a chat/file event should surface as a tray notification: (title, body).</summary>
    public event Action<string, string>? NotificationRequested;

    public MainViewModel(
        Account account,
        AppConfig config,
        StorageService storage,
        HistoryService history,
        DiscoveryService discovery,
        ConnectionService connections,
        FileTransferService fileTransfers,
        OutboxService outbox,
        UpdateService updates)
    {
        _account = account;
        _config = config;
        _storage = storage;
        _history = history;
        _discovery = discovery;
        _connections = connections;
        _fileTransfers = fileTransfers;
        _outbox = outbox;
        _updates = updates;

        MyName = account.DisplayName;
        MyId = account.UserId;

        _discovery.PeerDiscovered += OnPeerDiscovered;
        _connections.PeerConnected += OnPeerConnected;
        _connections.PeerDisconnected += OnPeerDisconnected;
        _connections.MessageReceived += OnMessageReceived;
        _fileTransfers.IncomingRequest += OnIncomingFileRequest;
        _fileTransfers.StateChanged += OnFileStateChanged;
        _outbox.ItemSent += OnOutboxItemSent;
        _outbox.ItemDropped += OnOutboxItemDropped;

        LoadContacts();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshOnline();
        _refreshTimer.Start();
    }

    // Раньше текст в поле ввода переживал переключение контакта: набрали сообщение одному
    // человеку, кликнули на другого — черновик оставался в поле и уходил не туда при следующем
    // нажатии «Отправить». Теперь черновик привязан к конкретному контакту и восстанавливается
    // при возврате к переписке, как в обычных мессенджерах.
    partial void OnSelectedContactChanging(Contact? oldValue, Contact? newValue)
    {
        if (oldValue is null)
            return;

        if (string.IsNullOrEmpty(MessageInput))
            _drafts.Remove(oldValue.UserId);
        else
            _drafts[oldValue.UserId] = MessageInput;
    }

    partial void OnSelectedContactChanged(Contact? value)
    {
        Messages.Clear();
        MessageInput = value is not null && _drafts.TryGetValue(value.UserId, out var draft) ? draft : "";

        if (value is null)
            return;

        value.UnreadCount = 0;
        foreach (var m in _storage.LoadHistory(value.UserId))
        {
            // Пометка в истории могла пережить аварийное завершение — источником правды остаётся очередь.
            if (m.Pending && !_outbox.ContainsMessage(value.UserId, m.Id))
                m.Pending = false;
            Messages.Add(m);
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = MessageInput?.Trim();
        if (string.IsNullOrEmpty(text) || SelectedContact is null)
            return;

        MessageInput = "";
        var contact = SelectedContact;
        StatusMessage = "";

        var pending = await QueueAndSendTextAsync(contact, text);
        if (pending > 0)
            StatusMessage = "Собеседник недоступен — сообщение уйдёт автоматически, когда он появится в сети";
    }

    /// <summary>Общий путь для любого исходящего текста: обычное сообщение из поля ввода
    /// и комментарий, приложенный к решению принять/отклонить файл, идут одинаково —
    /// сразу в историю и в очередь отправки. Возвращает, сколько элементов у собеседника
    /// осталось в очереди после попытки отправить.</summary>
    private async Task<int> QueueAndSendTextAsync(Contact contact, string text)
    {
        var message = new Message
        {
            Direction = MessageDirection.Outgoing,
            Kind = MessageKind.Text,
            Text = text,
            Pending = true
        };

        AppendMessage(contact.UserId, message, persist: true);
        _outbox.QueueText(contact.UserId, message);

        // Соединения может ещё не быть — очередь всё равно уже приняла сообщение и отправит его
        // сама, как только собеседник появится в сети.
        await _connections.EnsureConnectionAsync(ToPeer(contact));
        await _outbox.FlushAsync(contact.UserId);

        return _outbox.PendingCount(contact.UserId);
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        if (SelectedContact is null)
            return;

        var dialog = new OpenFileDialog { Title = "Выберите файл для отправки" };
        if (dialog.ShowDialog() != true)
            return;

        await SendFileAsync(dialog.FileName);
    }

    public async Task SendFileAsync(string filePath)
    {
        if (SelectedContact is null || !File.Exists(filePath))
            return;

        var contact = SelectedContact;
        _outbox.QueueFile(contact.UserId, filePath);
        StatusMessage = "";

        await _connections.EnsureConnectionAsync(ToPeer(contact));
        await _outbox.FlushAsync(contact.UserId);

        if (_outbox.PendingCount(contact.UserId) > 0)
            StatusMessage = $"Собеседник недоступен — файл «{Path.GetFileName(filePath)}» уйдёт, когда он появится в сети";
    }

    /// <summary>Вставка из буфера обмена по Ctrl+V в поле ввода: файл(ы) из проводника уходят как есть,
    /// растровое изображение (например, скриншот) сохраняется во временный PNG и отправляется как файл.
    /// Обычный текст в буфере не перехватывается — для него работает стандартная вставка в TextBox.</summary>
    public async Task SendClipboardImageAsync()
    {
        if (SelectedContact is null || !Clipboard.ContainsImage())
            return;

        var image = Clipboard.GetImage();
        if (image is null)
            return;

        var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                encoder.Save(fs);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось вставить изображение из буфера обмена: {ex.Message}";
            return;
        }

        await SendFileAsync(tempPath);
    }

    [RelayCommand]
    private async Task AddContactAsync()
    {
        var dialog = new AddContactWindow { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
            return;

        var address = dialog.Address?.Trim();
        if (string.IsNullOrEmpty(address))
            return;

        string ip;
        var port = _config.TcpPort;

        if (address.Contains(':'))
        {
            var parts = address.Split(':');
            ip = parts[0].Trim();
            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsed))
                port = parsed;
        }
        else
        {
            ip = address;
        }

        StatusMessage = "Подключение...";
        var peer = await _connections.ConnectToEndpointAsync(ip, port);

        if (peer is null)
        {
            StatusMessage = $"Не удалось подключиться к {address}";
            return;
        }

        StatusMessage = $"Подключено: {peer.DisplayName}";

        var contact = GetOrCreateContact(peer.UserId);
        contact.DisplayName = peer.DisplayName;
        contact.SigningPublicKey = peer.SigningPublicKey;
        contact.IpAddress = ip;
        contact.TcpPort = port;
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>Клик по устройству в списке «Устройства в сети»: считаем это намерением начать
    /// переписку, поэтому сразу переводим его в полноценные контакты (иначе список слева
    /// и выбор в нём рассинхронизируются — устройства в нём нет).</summary>
    [RelayCommand]
    private void OpenDevice(Contact? device)
    {
        if (device is null)
            return;

        device.IsKnownContact = true;
        ApplyFilter();
        SelectedContact = Contacts.FirstOrDefault(c => c.UserId == device.UserId) ?? device;
    }

    [RelayCommand]
    private void CopyContactId(Contact? contact)
    {
        if (contact is null)
            return;
        Clipboard.SetText(contact.UserId);
        StatusMessage = "ID скопирован в буфер обмена";
    }

    [RelayCommand]
    private void CopyMessage(Message? message)
    {
        if (message is null)
            return;

        var text = message.IsFile ? message.FileName : message.Text;
        if (string.IsNullOrEmpty(text))
            return;

        Clipboard.SetText(text);
        StatusMessage = "Сообщение скопировано в буфер обмена";
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_account.DisplayName, _config.RetentionDays, _config.TcpPort)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
            return;

        if (!string.IsNullOrWhiteSpace(dialog.DisplayName) && dialog.DisplayName != _account.DisplayName)
        {
            _account.DisplayName = dialog.DisplayName;
            _storage.SaveAccount(_account);
            MyName = _account.DisplayName;
        }

        _config.RetentionDays = dialog.RetentionDays;
        _config.TcpPort = dialog.TcpPort;
        _storage.SaveConfig(_config);
        _history.PruneExpired(_config.RetentionDays);
        StatusMessage = "Настройки сохранены (порт применится после перезапуска)";
    }

    /// <summary>Тихая фоновая проверка при старте: без сети/GitHub недоступен — просто ничего
    /// не показываем, это не должно мешать работе мессенджера.</summary>
    public async Task CheckForUpdatesAsync()
    {
        var info = await _updates.CheckForUpdateAsync();
        if (info is null)
            return;

        _pendingUpdate = info;
        UpdateBannerText = $"Доступна новая версия {info.Version} (сейчас {_updates.CurrentVersion})";
        UpdateAvailable = true;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null || UpdateInProgress)
            return;

        UpdateInProgress = true;
        UpdateProgress = 0;

        try
        {
            var progress = new Progress<double>(p => UpdateProgress = p);
            await _updates.ApplyUpdateAsync(_pendingUpdate, progress);

            // Файл обновления уже скачан, скрипт-подстановщик запущен и ждёт закрытия этого
            // процесса. Закрываем окно по-настоящему (не сворачиваем в трей), чтобы exe освободился.
            CloseForUpdateRequested?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateInProgress = false;
            StatusMessage = $"Не удалось установить обновление: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (_pendingUpdate is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_pendingUpdate.ReleaseUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    /// <summary>Приложение сворачивается в трей по крестику — для реальной установки обновления
    /// нужно, чтобы процесс завершился по-настоящему, поэтому окно просит об этом явно.</summary>
    public event Action? CloseForUpdateRequested;

    private void OnPeerDiscovered(Peer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var contact = GetOrCreateContact(peer.UserId);
            contact.DisplayName = peer.DisplayName;
            contact.SigningPublicKey = peer.SigningPublicKey;
            contact.IpAddress = peer.IpAddress;
            contact.TcpPort = peer.TcpPort;
            contact.LastSeenUtc = peer.LastSeenUtc;
            _ = _connections.EnsureConnectionAsync(peer);
        });
    }

    private void OnPeerConnected(Peer peer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var contact = GetOrCreateContact(peer.UserId);
            contact.DisplayName = peer.DisplayName;
            contact.SigningPublicKey = peer.SigningPublicKey;
            if (!string.IsNullOrEmpty(peer.IpAddress))
                contact.IpAddress = peer.IpAddress;
            contact.LastSeenUtc = DateTime.UtcNow;

            // Подтверждённое TCP-рукопожатие (не просто UDP-маячок) — теперь это настоящий контакт,
            // а не просто «устройство в сети».
            var wasKnown = contact.IsKnownContact;
            contact.IsKnownContact = true;
            if (!wasKnown)
                ApplyFilter();
        });
    }

    private void OnPeerDisconnected(string peerId)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_contacts.TryGetValue(peerId, out var contact))
                contact.IsOnline = false;
        });
    }

    private void OnMessageReceived(string peerId, Message message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var contact = GetOrCreateContact(peerId);
            contact.LastMessageUtc = message.TimestampUtc;
            contact.LastMessagePreview = message.DisplayText;
            _history.AppendMessage(peerId, message);

            var isActiveChat = SelectedContact?.UserId == peerId;
            if (isActiveChat)
            {
                Messages.Add(message);
                contact.UnreadCount = 0;
            }
            else
            {
                contact.UnreadCount++;
            }

            if (!isActiveChat || IsWindowActive?.Invoke() != true)
                NotificationRequested?.Invoke(contact.DisplayName, message.DisplayText);
        });
    }

    private void OnOutboxItemSent(string peerId, OutboxItem item)
    {
        if (item.Kind != OutboxKind.Text || item.Message is null)
            return;

        var messageId = item.Message.Id;
        _history.MarkSent(peerId, messageId);

        Application.Current.Dispatcher.Invoke(() =>
        {
            item.Message.Pending = false;

            if (SelectedContact?.UserId == peerId)
            {
                var shown = Messages.FirstOrDefault(m => m.Id == messageId);
                if (shown is not null)
                    shown.Pending = false;
            }
        });
    }

    private void OnOutboxItemDropped(string peerId, OutboxItem item, string reason)
    {
        var what = item.Kind == OutboxKind.File
            ? $"файл «{Path.GetFileName(item.FilePath)}»"
            : "сообщение";

        Application.Current.Dispatcher.Invoke(() =>
            StatusMessage = $"Не удалось отправить {what}: {reason}");
    }

    private void OnIncomingFileRequest(FileTransfer transfer)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var contact = GetOrCreateContact(transfer.PeerId);

            var dialog = new FileResponseWindow(contact.DisplayName, transfer.FileName, transfer.Size)
            {
                Owner = Application.Current.MainWindow
            };

            // Закрытие крестиком равносильно отказу без комментария.
            var closedExplicitly = dialog.ShowDialog() == true;
            var accepted = closedExplicitly && dialog.Accepted;
            var comment = closedExplicitly ? dialog.Comment : "";

            if (accepted)
            {
                var save = new SaveFileDialog { FileName = transfer.FileName, Title = "Куда сохранить файл" };
                if (save.ShowDialog() == true)
                    _fileTransfers.AcceptTransfer(transfer.TransferId, save.FileName);
                else
                    _fileTransfers.DeclineTransfer(transfer.TransferId);
            }
            else
            {
                _fileTransfers.DeclineTransfer(transfer.TransferId);
            }

            if (!string.IsNullOrEmpty(comment))
                _ = QueueAndSendTextAsync(contact, comment);
        });
    }

    private void OnFileStateChanged(FileTransfer transfer)
    {
        switch (transfer.Status)
        {
            case FileTransferStatus.Completed:
                HandleFileCompleted(transfer);
                break;

            // Приём отклонил сам получатель — он и так знает об этом из своего диалога,
            // системная запись здесь только продублирует то, что он только что решил.
            case FileTransferStatus.Declined when transfer.IsSender:
            case FileTransferStatus.Canceled:
            case FileTransferStatus.Failed:
                HandleFileTerminalNonSuccess(transfer);
                break;
        }
    }

    private void HandleFileCompleted(FileTransfer transfer)
    {
        if (!_historyRecorded.Add(transfer.TransferId))
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var message = new Message
            {
                Direction = transfer.IsSender ? MessageDirection.Outgoing : MessageDirection.Incoming,
                Kind = MessageKind.File,
                FileName = transfer.FileName,
                FileSize = transfer.Size,
                FileSha256 = transfer.Sha256,
                SavedPath = transfer.IsSender ? transfer.SourcePath : transfer.SavePath
            };

            AppendMessage(transfer.PeerId, message, persist: true);

            if (!transfer.IsSender)
                NotifyIfInactive(transfer.PeerId, message.DisplayText);
        });
    }

    /// <summary>Раньше отклонённая/отменённая/сорвавшаяся передача просто исчезала из списка
    /// внизу без объяснений — собеседник не понимал, принял файл человек или нет. Теперь по
    /// каждому такому исходу в переписке остаётся заметная запись, видимая обеим сторонам.</summary>
    private void HandleFileTerminalNonSuccess(FileTransfer transfer)
    {
        if (!_historyRecorded.Add(transfer.TransferId))
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var text = (transfer.Status, transfer.IsSender) switch
            {
                (FileTransferStatus.Declined, true) => $"Файл «{transfer.FileName}» отклонён собеседником",
                (FileTransferStatus.Canceled, true) => $"Передача файла «{transfer.FileName}» отменена собеседником",
                (FileTransferStatus.Canceled, false) => $"Собеседник отменил передачу файла «{transfer.FileName}»",
                (FileTransferStatus.Failed, true) => $"Не удалось отправить файл «{transfer.FileName}»",
                (FileTransferStatus.Failed, false) => $"Не удалось получить файл «{transfer.FileName}»",
                _ => $"Файл «{transfer.FileName}»: передача не завершена"
            };

            var message = new Message
            {
                Direction = transfer.IsSender ? MessageDirection.Outgoing : MessageDirection.Incoming,
                Kind = MessageKind.System,
                Text = text
            };

            AppendMessage(transfer.PeerId, message, persist: true);
            NotifyIfInactive(transfer.PeerId, text);
        });
    }

    private void NotifyIfInactive(string peerId, string body)
    {
        var isActiveChat = SelectedContact?.UserId == peerId;
        if (isActiveChat && IsWindowActive?.Invoke() == true)
            return;

        var name = _contacts.TryGetValue(peerId, out var c) ? c.DisplayName : ShortId(peerId);
        NotificationRequested?.Invoke(name, body);
    }

    [RelayCommand]
    private void OpenFile(Message? message)
    {
        if (message is null || !message.IsFile)
            return;

        var path = message.SavedPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            StatusMessage = "Файл не найден на диске";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось открыть файл: {ex.Message}";
        }
    }

    private void AppendMessage(string peerId, Message message, bool persist)
    {
        var contact = GetOrCreateContact(peerId);
        contact.LastMessageUtc = message.TimestampUtc;
        contact.LastMessagePreview = message.DisplayText;

        if (persist)
            _history.AppendMessage(peerId, message);

        if (SelectedContact?.UserId == peerId)
            Messages.Add(message);
    }

    private Contact GetOrCreateContact(string userId)
    {
        if (_contacts.TryGetValue(userId, out var existing))
            return existing;

        var peers = _storage.LoadPeers();
        var peer = peers.TryGetValue(userId, out var p) ? p : null;

        var contact = new Contact
        {
            UserId = userId,
            DisplayName = peer?.DisplayName ?? ShortId(userId),
            SigningPublicKey = peer?.SigningPublicKey ?? "",
            IpAddress = peer?.IpAddress ?? "",
            TcpPort = peer?.TcpPort ?? 0,
            LastSeenUtc = peer?.LastSeenUtc ?? DateTime.MinValue
        };

        _contacts[userId] = contact;
        ApplyFilter();
        return contact;
    }

    private void LoadContacts()
    {
        var peers = _storage.LoadPeers();
        foreach (var (id, peer) in peers)
        {
            // peers.json пишется только после успешного рукопожатия — раз запись здесь есть,
            // это уже подтверждённый контакт, а не просто устройство, замеченное в эфире.
            _contacts[id] = new Contact
            {
                UserId = id,
                DisplayName = peer.DisplayName,
                SigningPublicKey = peer.SigningPublicKey,
                IpAddress = peer.IpAddress,
                TcpPort = peer.TcpPort,
                LastSeenUtc = peer.LastSeenUtc,
                IsKnownContact = true
            };
        }

        foreach (var id in _storage.ListHistoryPeerIds())
        {
            if (_contacts.ContainsKey(id))
                continue;
            _contacts[id] = new Contact { UserId = id, DisplayName = ShortId(id), IsKnownContact = true };
        }

        foreach (var id in _contacts.Keys.ToList())
        {
            var history = _storage.LoadHistory(id);
            if (history.Count == 0)
                continue;

            var last = history[^1];
            _contacts[id].LastMessageUtc = last.TimestampUtc;
            _contacts[id].LastMessagePreview = last.DisplayText;
        }

        ApplyFilter();
    }

    private void RefreshOnline()
    {
        var discovered = _discovery.GetDiscovered();
        var cutoff = DateTime.UtcNow.AddSeconds(-15);

        foreach (var contact in _contacts.Values)
        {
            var found = discovered.FirstOrDefault(p => p.UserId == contact.UserId);
            var online = found is not null && found.LastSeenUtc >= cutoff;

            contact.IsOnline = online;
            if (online)
            {
                contact.IpAddress = found!.IpAddress;
                contact.TcpPort = found.TcpPort;
                contact.LastSeenUtc = found.LastSeenUtc;
                contact.StatusText = "онлайн";
                _ = _connections.EnsureConnectionAsync(found);
            }
            else
            {
                contact.StatusText = contact.LastSeenUtc == DateTime.MinValue
                    ? "офлайн"
                    : $"был(а) {contact.LastSeenUtc.ToLocalTime():g}";
            }
        }

        // «Устройства в сети» — список только онлайн-устройств, поэтому пересчитываем его
        // на каждом тике: пропавшее из эфира устройство должно исчезнуть из списка само.
        ApplyFilter();
    }

    /// <summary>Адаптивный поиск: разбивает запрос на слова и требует, чтобы каждое слово
    /// нашлось хоть где-то — в имени, ID или IP. Так «серг 192» находит «Сергей» по адресу
    /// 192.168.x.x, а не только точное совпадение с начала строки, как раньше. Заодно делит
    /// контакты на «Контакты» (есть история/подключение) и «Устройства в сети» (только что
    /// обнаружены, диалога ещё не было).</summary>
    private void ApplyFilter()
    {
        var terms = (SearchText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool Matches(Contact c) => terms.All(t =>
            c.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            c.UserId.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            c.IpAddress.Contains(t, StringComparison.OrdinalIgnoreCase));

        var sortedContacts = _contacts.Values
            .Where(c => c.IsKnownContact && Matches(c))
            .OrderByDescending(c => c.IsOnline)
            .ThenByDescending(c => c.LastMessageUtc ?? DateTime.MinValue)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sortedDevices = _contacts.Values
            .Where(c => !c.IsKnownContact && c.IsOnline && Matches(c))
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SyncCollection(Contacts, sortedContacts);
        SyncCollection(Devices, sortedDevices);
    }

    /// <summary>Приводит коллекцию к целевому списку точечными Insert/Remove/Move вместо
    /// Clear()+Add(). Это критично: Clear() шлёт ListBox событие Reset, из-за которого WPF
    /// на мгновение обнуляет SelectedItem (через двустороннюю привязку это долетает до
    /// SelectedContact = null и обратно) — и ровно это раньше сбрасывало курсор в начале
    /// поля ввода на каждом тике таймера обновления статусов, даже когда список контактов
    /// по сути не менялся. Точечные операции такого сброса не вызывают.</summary>
    private static void SyncCollection<T>(ObservableCollection<T> collection, IReadOnlyList<T> target)
    {
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            var currentIndex = collection.IndexOf(item);
            if (currentIndex < 0)
                collection.Insert(i, item);
            else if (currentIndex != i)
                collection.Move(currentIndex, i);
        }
    }

    private static Peer ToPeer(Contact contact) => new()
    {
        UserId = contact.UserId,
        DisplayName = contact.DisplayName,
        SigningPublicKey = contact.SigningPublicKey,
        IpAddress = contact.IpAddress,
        TcpPort = contact.TcpPort
    };

    private static string ShortId(string userId) =>
        userId.Length <= 8 ? userId : userId[..8];
}
