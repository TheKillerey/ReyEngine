using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ReyEngine.App.Views;

/// <summary>M115: converters for the skin-grouped animation browser.</summary>
public static class M115Converters
{
    /// <summary>true → the transparent-green "this skin uses this clip" row tint; false → no background.</summary>
    public static readonly IValueConverter CurrentSkinBrush = new FuncValueConverter<bool, IBrush?>(
        isCurrent => isCurrent ? new SolidColorBrush(Color.FromArgb(0x38, 0x2E, 0xCC, 0x71)) : null);
}
