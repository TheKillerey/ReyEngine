using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ReyEngine.Core.Diagnostics;

namespace ReyEngine.App.Converters;

public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FFC857"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#36E2C2"));
    private static readonly IBrush Info = new SolidColorBrush(Color.Parse("#9AA7B8"));
    private static readonly IBrush Trace = new SolidColorBrush(Color.Parse("#5A6678"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => Error,
        LogLevel.Warning => Warning,
        LogLevel.Success => Success,
        LogLevel.Trace => Trace,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
