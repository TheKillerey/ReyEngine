namespace ReyEngine.App.Services;

public sealed record ExperimentalShaderAsset(string Path, byte[] Bytes);

/// <summary>Validated companion-cache content plus the exact materials it safely covers.</summary>
public sealed record ExperimentalLightmapShaderPatch(
    IReadOnlyList<ExperimentalShaderAsset> Assets,
    IReadOnlySet<string> SupportedMaterials,
    int VertexKeysAdded,
    int PixelKeysAdded,
    int CustomBlobsAdded,
    string Detail);
