namespace ReyEngine.Formats.Materials;

/// <summary>Riot's authored color blend-factor enum used by StaticMaterialDef passes.</summary>
public enum MaterialBlendFactor
{
    Zero = 0,
    One = 1,
    SourceColor = 2,
    OneMinusSourceColor = 3,
    DestinationColor = 4,
    OneMinusDestinationColor = 5,
    SourceAlpha = 6,
    OneMinusSourceAlpha = 7,
    DestinationAlpha = 8,
    OneMinusDestinationAlpha = 9,
}

public static class MaterialBlendFactors
{
    /// <summary>An absent source factor uses the render API default: One.</summary>
    public static MaterialBlendFactor Source(int authored) =>
        Enum.IsDefined(typeof(MaterialBlendFactor), authored)
            ? (MaterialBlendFactor)authored
            : MaterialBlendFactor.One;

    /// <summary>An absent destination factor uses the render API default: Zero.</summary>
    public static MaterialBlendFactor Destination(int authored) =>
        Enum.IsDefined(typeof(MaterialBlendFactor), authored)
            ? (MaterialBlendFactor)authored
            : MaterialBlendFactor.Zero;
}
