namespace ReyEngine.Formats.MapGeo;

/// <summary>A named visibility bit (a dragon elemental-rift layer or a baron pit state).</summary>
public readonly record struct VisibilityLayer(string Name, int Bit);

/// <summary>
/// League map visibility: the dragon (elemental rift) layer system — an 8-bit per-mesh bitmask
/// (<c>EnvironmentVisibility</c> Layer1..Layer8) — and the baron-pit override states. Mirrors the
/// MapgeoAddon visibility system: a mesh with flags 0 or 255 shows on every dragon configuration.
/// </summary>
public static class MapVisibility
{
    public static readonly VisibilityLayer[] Dragons =
    {
        new("Base", 1), new("Inferno", 2), new("Mountain", 4), new("Ocean", 8),
        new("Cloud", 16), new("Hextech", 32), new("Chemtech", 64), new("Void", 128),
    };

    public static readonly VisibilityLayer[] Barons =
    {
        new("Base", 1), new("Cup", 2), new("Tunnel", 4), new("Upgraded", 8),
    };

    /// <summary>The Base layer bit (Layer1) — the "no elemental dragon" state.</summary>
    public const int BaseBit = 1;

    /// <summary>
    /// Visible for the chosen dragon layer? A mesh shows for dragon state <paramref name="dragonBit"/> iff
    /// that layer's bit is set in its <paramref name="flags"/> mask (matching how the game activates a single
    /// layer bit at a time). <c>dragonBit == 0</c> means "All" (no filter); <c>flags == 0</c> means the mesh
    /// carries no layer info and is unconstrained. Meshes flagged for all layers (255) match every dragon.
    /// (M33 fix: the old "Base bit forces visible everywhere" override wrongly kept Base/multi-layer meshes
    /// on for every dragon — verified against Map11's flag histogram where base geometry is swapped per layer.)
    /// </summary>
    public static bool VisibleForDragon(int flags, int dragonBit)
        => dragonBit == 0
           || flags == 0
           || (flags & dragonBit) != 0;

    /// <summary>Human-readable layer label for a mesh's bitmask (used for the layer-group tree).</summary>
    public static string DragonLabel(int flags)
    {
        if (flags == 0 || flags == 255) return "All Layers";
        var names = Dragons.Where(d => (flags & d.Bit) != 0).Select(d => d.Name).ToList();
        return names.Count == 0 ? $"Layer {flags}" : string.Join(" · ", names);
    }
}
