using System.Windows;

namespace p2p.Views;

public partial class AccountSetupWindow : Window
{
    public string DisplayName => NameBox.Text.Trim();

    public AccountSetupWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            MessageBox.Show("Введите имя.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
