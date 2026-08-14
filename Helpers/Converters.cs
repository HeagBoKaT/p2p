using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using p2p.Models;

namespace p2p.Helpers;

public class MessageAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class MessageBackgroundConverter : IValueConverter
{
    private static readonly Brush Outgoing = new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF));
    private static readonly Brush Incoming = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value ? Outgoing : Incoming;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class UnreadVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (int)value > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => !(value is bool b && b);
}

public class FileTransferStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FileTransfer t)
            return "";

        var direction = t.IsSender ? "отправка" : "получение";

        return t.Status switch
        {
            FileTransferStatus.Requested => $"{direction}: ожидание ответа…",
            FileTransferStatus.Accepted => $"{direction}: подготовка…",
            FileTransferStatus.InProgress => string.IsNullOrEmpty(t.SpeedText)
                ? $"{direction}: {t.Progress:P0}"
                : $"{direction}: {t.Progress:P0} · {t.SpeedText}",
            FileTransferStatus.Completed => "завершено",
            FileTransferStatus.Declined => "отклонено",
            FileTransferStatus.Failed => "ошибка",
            FileTransferStatus.Canceled => "отменено",
            _ => ""
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
