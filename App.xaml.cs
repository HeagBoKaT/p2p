using System.Diagnostics;
using System.Windows;
using p2p.Models;
using p2p.Services;
using p2p.ViewModels;
using p2p.Views;

namespace p2p;

public partial class App : Application
{
    private DiscoveryService? _discovery;
    private ConnectionService? _connections;
    private FileTransferService? _fileTransfers;
    private OutboxService? _outbox;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var (portOverride, dataDir, autoAccountName, noDiscovery) = ParseArgs(e.Args);

        var storage = new StorageService(dataDir);
        var config = storage.LoadConfig();
        if (portOverride.HasValue)
            config.TcpPort = portOverride.Value;

        var account = storage.LoadAccount();
        if (account is null)
        {
            // --auto-account: для проверок и Docker-контейнеров без интерактивной desktop-сессии —
            // диалог создания аккаунта там некому кликнуть. Только не для обычного запуска: без
            // этого флага поведение для реального пользователя не меняется вовсе.
            if (autoAccountName is not null)
            {
                account = CreateAccount(autoAccountName);
                storage.SaveAccount(account);
            }
            else
            {
                var setup = new AccountSetupWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                account = CreateAccount(setup.DisplayName);
                storage.SaveAccount(account);
            }
        }

        string signingPrivateKey;
        try
        {
            signingPrivateKey = CryptoService.UnprotectPrivateKey(account.SigningPrivateKeyEncrypted);
        }
        catch
        {
            MessageBox.Show("Не удалось расшифровать локальный ключ учётной записи.", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var history = new HistoryService(storage);
        history.PruneExpired(config.RetentionDays);

        // --no-discovery: то, из-за чего проверки на хосте раньше «шумели» в реальной LAN — этот
        // UDP-маячок каждые 4 секунды делает тестовый аккаунт видимым всем настоящим устройствам
        // в сети. Заодно пропускаем и правила брандмауэра — незачем плодить их для одноразовых
        // тестовых запусков. TCP-соединения при этом продолжают работать как обычно (просто никто
        // не узнает об этом экземпляре сам — можно законнектиться вручную через --data-dir/--port).
        if (!noDiscovery)
            TryRegisterFirewallRules(config.TcpPort, config.DiscoveryPort);
        var updates = new UpdateService();

        try
        {
            _discovery = new DiscoveryService(account, config);
            _connections = new ConnectionService(account, signingPrivateKey, config, storage);
            _fileTransfers = new FileTransferService(_connections);
            _outbox = new OutboxService(storage, _connections, _fileTransfers);

            if (!noDiscovery)
                _discovery.Start();
            _connections.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить сетевые службы (возможно, порт {config.TcpPort} занят другой программой).\n\n{ex.Message}",
                "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var viewModel = new MainViewModel(account, config, storage, history, _discovery, _connections, _fileTransfers, _outbox, updates);
        var mainWindow = new MainWindow { DataContext = viewModel };
        MainWindow = mainWindow;
        mainWindow.Show();

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Проверка версии не должна задерживать запуск — уходит в фон сразу после показа окна.
        _ = viewModel.CheckForUpdatesAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _discovery?.Dispose();
        _connections?.Dispose();
        _fileTransfers?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Произошла непредвиденная ошибка:\n\n{e.Exception.Message}", "Ошибка",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            MessageBox.Show($"Критическая ошибка:\n\n{ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }

    private static (int? Port, string? DataDir, string? AutoAccountName, bool NoDiscovery) ParseArgs(string[] args)
    {
        int? port = null;
        string? dataDir = null;
        string? autoAccountName = null;
        var noDiscovery = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                port = p;
                i++;
            }
            else if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                dataDir = args[i + 1];
                i++;
            }
            else if (args[i] == "--auto-account" && i + 1 < args.Length)
            {
                autoAccountName = args[i + 1];
                i++;
            }
            else if (args[i] == "--no-discovery")
            {
                noDiscovery = true;
            }
        }

        return (port, dataDir, autoAccountName, noDiscovery);
    }

    private static Account CreateAccount(string displayName)
    {
        var (publicKey, privateKey) = CryptoService.GenerateSigningKeyPair();

        return new Account
        {
            UserId = Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            SigningPublicKey = publicKey,
            SigningPrivateKeyEncrypted = CryptoService.ProtectPrivateKey(privateKey)
        };
    }

    private static void TryRegisterFirewallRules(int tcpPort, int udpPort)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "p2p.exe";
                RunNetsh("advfirewall firewall delete rule name=\"p2p TCP\"");
                RunNetsh("advfirewall firewall delete rule name=\"p2p UDP\"");
                RunNetsh($"advfirewall firewall add rule name=\"p2p TCP\" dir=in action=allow protocol=TCP localport={tcpPort} program=\"{exe}\"");
                RunNetsh($"advfirewall firewall add rule name=\"p2p UDP\" dir=in action=allow protocol=UDP localport={udpPort} program=\"{exe}\"");
            }
            catch
            {
            }
        });
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(3000);
    }
}
