using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using p2p.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace p2p;

public partial class MainWindow : Window
{
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _minimizedNoticeShown;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (DataContext is MainViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.NotificationRequested += OnNotificationRequested;
            vm.CloseForUpdateRequested += () => { _allowClose = true; Close(); };
            vm.IsWindowActive = () => IsActive;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedContact))
            ScrollToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (MessagesList.Items.Count > 0)
            MessagesList.ScrollIntoView(MessagesList.Items[^1]);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (HasFiles(e))
            DropOverlay.Visibility = Visibility.Visible;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (DataContext is MainViewModel vm &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
                _ = vm.SendFileAsync(file);
        }
    }

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    /// <summary>Ctrl+V в поле ввода: файлы из проводника и картинки (например, скриншот) уходят как
    /// вложение вместо вставки их текстового представления. Обычный текст в буфере не перехватывается —
    /// для него отрабатывает штатная вставка TextBox.</summary>
    private void MessageInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (DataContext is not MainViewModel vm)
            return;

        if (Clipboard.ContainsFileDropList())
        {
            e.Handled = true;
            foreach (var file in Clipboard.GetFileDropList())
                if (file is not null)
                    _ = vm.SendFileAsync(file);
        }
        else if (Clipboard.ContainsImage())
        {
            e.Handled = true;
            _ = vm.SendClipboardImageAsync();
        }
    }

    // ----- System tray -----

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitFromTray());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "АнтиПигвин",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is not null)
                return new Drawing.Icon(info.Stream);
        }
        catch
        {
        }

        return Drawing.SystemIcons.Application;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            return;

        Hide();

        if (_minimizedNoticeShown)
            return;

        _minimizedNoticeShown = true;
        _trayIcon?.ShowBalloonTip(2500, "АнтиПигвин",
            "Приложение свёрнуто в трей. Дважды щёлкните по значку, чтобы открыть.", Forms.ToolTipIcon.Info);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            return;
        }

        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private void OnNotificationRequested(string title, string body)
    {
        _trayIcon?.ShowBalloonTip(4000, title, body, Forms.ToolTipIcon.Info);
    }
}
