using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using McDownloader.Models;

namespace McDownloader.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = !string.IsNullOrWhiteSpace(value as string);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>任务状态 → 徽标底色（parameter=bg）或文字色。</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is DownloadState s ? s : DownloadState.Pending;
        var isBackground = (parameter as string)?.Equals("bg", StringComparison.OrdinalIgnoreCase) == true;
        var color = isBackground
            ? state switch
            {
                DownloadState.Running => "#DCFCE7",
                DownloadState.Succeeded => "#DCFCE7",
                DownloadState.Failed => "#FEE2E2",
                DownloadState.Canceled => "#F3F4F6",
                _ => "#F3F4F6"
            }
            : state switch
            {
                DownloadState.Running => "#16A34A",
                DownloadState.Succeeded => "#15803D",
                DownloadState.Failed => "#EF4444",
                DownloadState.Canceled => "#9CA3AF",
                _ => "#6B7280"
            };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>网络图片 → ImageSource（异步加载，失败静默）。</summary>
public sealed class ImageUrlConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.DecodePixelWidth = 96;
            bitmap.DownloadFailed += (_, _) => { };
            bitmap.EndInit();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class JavaRuntimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is JavaRuntime runtime ? runtime.Display : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
