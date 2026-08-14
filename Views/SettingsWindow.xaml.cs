using System.Windows;

namespace p2p.Views;

public partial class SettingsWindow : Window
{
    public string DisplayName { get; private set; } = "";
    public int RetentionDays { get; private set; }
    public int TcpPort { get; private set; }

    public SettingsWindow(string displayName, int retentionDays, int tcpPort)
    {
        InitializeComponent();
        DisplayNameBox.Text = displayName;
        RetentionDaysBox.Text = retentionDays.ToString();
        TcpPortBox.Text = tcpPort.ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = DisplayNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Введите имя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(RetentionDaysBox.Text.Trim(), out var days) || days < 1 || days > 30)
        {
            MessageBox.Show("Хранение истории должно быть числом от 1 до 30 дней.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(TcpPortBox.Text.Trim(), out var port) || port < 1024 || port > 65535)
        {
            MessageBox.Show("Порт должен быть числом от 1024 до 65535.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DisplayName = name;
        RetentionDays = days;
        TcpPort = port;
        DialogResult = true;
    }
}
