using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ReyEngine.App.Converters;

/// <summary>
/// M50c: "r, g, b(, a)" text (League params, 0..1 floats) → a solid brush for a colour-swatch preview.
/// The swatch is shown fully opaque (alpha in the data often means intensity, and an invisible swatch
/// helps nobody); unparsable text → transparent.
/// </summary>
public sealed class VecColorBrushConverter : IValueConverter
{
    public static readonly VecColorBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Brushes.Transparent;
        var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return Brushes.Transparent;
        static byte Chan(string t) =>
            float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
                ? (byte)Math.Clamp(f * 255f, 0f, 255f) : (byte)0;
        return new SolidColorBrush(Color.FromRgb(Chan(parts[0]), Chan(parts[1]), Chan(parts[2])));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
