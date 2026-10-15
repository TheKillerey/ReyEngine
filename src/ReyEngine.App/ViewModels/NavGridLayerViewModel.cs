using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M565: one flag the loaded navgrid actually contains, as a layer the user can switch on and off.
///
/// <para>Deliberately unnamed. M562 labelled 0x0004 "bush" from the shape of its clusters and the
/// reporter, looking at those cells drawn on their own map, identified them as the area only one team may
/// walk. Guessing a name from a distribution is how that happened, so the editor now shows the mask, the
/// count and the colour, and leaves the naming to whoever is looking at the map.</para>
/// </summary>
public sealed partial class NavGridLayerViewModel : ObservableObject
{
    public required ushort Mask { get; init; }
    public required int Cells { get; init; }
    public required double Share { get; init; }
    public required Vector4 Color { get; init; }

    /// <summary>Raised when the user toggles this layer, so the overlay can be rebuilt.</summary>
    public Action? Changed { get; init; }

    [ObservableProperty] private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => Changed?.Invoke();

    public int Bit => System.Numerics.BitOperations.TrailingZeroCount(Mask);
    /// <summary>M568: the name comes from LOOKING at the layer on a map, not from its distribution.
    /// A flag nobody has identified shows its mask alone rather than a guess.</summary>
    public string Label => LabelOf(Mask);

    /// <summary>The label of a flag layer - the same text the editor's list and the Blender link show.</summary>
    public static string LabelOf(ushort mask)
    {
        int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
        return ReyEngine.Formats.MapGeo.NavGrid.LabelFor(mask) is { } named
            ? $"{named}   (bit {bit})"
            : $"0x{mask:x4}  ·  bit {bit}  ·  unidentified";
    }
    public string Detail => $"{Cells:n0} cells · {Share:P1}";

    /// <summary>The layer's colour as a brush, so the list can show which is which in the viewport.</summary>
    public Avalonia.Media.IBrush Swatch => new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromRgb((byte)(Color.X * 255), (byte)(Color.Y * 255), (byte)(Color.Z * 255)));
}
