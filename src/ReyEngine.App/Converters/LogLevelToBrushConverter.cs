using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ReyEngine.Core.Diagnostics;

namespace ReyEngine.App.Converters;

/// <summary>Console line colours. M686: the palette's signal brushes, looked up per line so they follow
/// the theme (the old constants were Kalista's cyan and greys under every palette).</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Error => ReyEngine.App.Services.ThemeService.Brush("ReyErrorBrush", "#FF6B6B"),
        LogLevel.Warning => ReyEngine.App.Services.ThemeService.Brush("ReyWarningBrush", "#FFC857"),
        LogLevel.Success => ReyEngine.App.Services.ThemeService.Brush("ReySuccessBrush", "#3DD68C"),
        LogLevel.Trace => ReyEngine.App.Services.ThemeService.Brush("ReyTextDimBrush", "#5A6678"),
        _ => ReyEngine.App.Services.ThemeService.Brush("ReyTextBrush", "#9AA7B8"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
