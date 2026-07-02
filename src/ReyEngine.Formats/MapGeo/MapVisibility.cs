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

    /// <summary>The Base layer bit (Layer1) — the always-present foundation ("no elemental dragon" state).</summary>
    public const int BaseBit = 1;

    /// <summary>
    /// Dragon visibility from the per-mesh layer bitmask ALONE (the "no controller" path), mirroring the
    /// MapgeoAddon's <c>update_environment_visibility</c>: <c>dragonBit == 0</c> means "All"; flags 0/255 are
    /// unconstrained; the <see cref="BaseBit"/> foundation is visible under every dragon; otherwise the mesh
    /// shows only when its own dragon bit matches. NOTE: a mesh's visibility CONTROLLER can override this and
    /// disable even a Base mesh — that override lives in <see cref="MapVisibilityResolver"/>, not here.
    /// </summary>
    public static bool VisibleForDragon(int flags, int dragonBit)
        => dragonBit == 0
           || flags == 0 || flags == 255
           || (flags & BaseBit) != 0
           || (flags & dragonBit) != 0;

    /// <summary>Human-readable layer label for a mesh's bitmask (used for the layer-group tree).</summary>
    public static string DragonLabel(int flags)
    {
        if (flags == 0 || flags == 255) return "All Layers";
        var names = Dragons.Where(d => (flags & d.Bit) != 0).Select(d => d.Name).ToList();
        return names.Count == 0 ? $"Layer {flags}" : string.Join(" · ", names);
    }
}
