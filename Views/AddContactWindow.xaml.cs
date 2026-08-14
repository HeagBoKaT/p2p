using System.Windows;

namespace p2p.Views;

public partial class AddContactWindow : Window
{
    public string Address => AddressBox.Text.Trim();

    public AddContactWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            MessageBox.Show("Введите IP-адрес.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
