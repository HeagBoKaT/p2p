using System.Windows;
using p2p.Models;

namespace p2p.Views;

public partial class FileResponseWindow : Window
{
    /// <summary>true — нажали «Принять», false — «Отклонить». DialogResult отличает эти два
    /// исхода от закрытия окна крестиком (которое трактуется как отказ без комментария).</summary>
    public bool Accepted { get; private set; }

    public string Comment => CommentBox.Text.Trim();

    public FileResponseWindow(string peerName, string fileName, long size)
    {
        InitializeComponent();
        SubText.Text = $"{peerName} хочет отправить файл:\n{fileName} ({Message.FormatBytes(size)})";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        DialogResult = true;
    }
}
