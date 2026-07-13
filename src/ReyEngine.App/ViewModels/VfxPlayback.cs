using System.Collections.Generic;
using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// One placed VFX system to play (M36): the parsed system, the world position of its placement, and one
/// resolved sprite texture per emitter (aligned to <see cref="VfxSystemDefinition.Emitters"/>; null → the
/// viewport uses a procedural soft-dot fallback). The same <see cref="TextureImage"/> instance is reused
/// across placements of the same system so the viewport uploads each sprite only once.
/// </summary>
public sealed record VfxPlaybackItem(
    VfxSystemDefinition System,
    Matrix4x4 Transform,
    IReadOnlyList<TextureImage?> EmitterTextures,
    IReadOnlyList<ReyEngine.Formats.Meshes.StaticMeshData?>? EmitterMeshes = null,
    IReadOnlyList<TextureImage?>? EmitterMultTextures = null,   // Riot TEXTUREMULT stage
    IReadOnlyList<TextureImage?>? EmitterDistortionTextures = null)
{
    /// <summary>Convenience for champion/editor previews authored at a translated root.</summary>
    public VfxPlaybackItem(VfxSystemDefinition system, Vector3 worldPos,
        IReadOnlyList<TextureImage?> emitterTextures,
        IReadOnlyList<ReyEngine.Formats.Meshes.StaticMeshData?>? emitterMeshes = null,
        IReadOnlyList<TextureImage?>? emitterMultTextures = null,
        IReadOnlyList<TextureImage?>? emitterDistortionTextures = null)
        : this(system, Matrix4x4.CreateTranslation(worldPos), emitterTextures, emitterMeshes, emitterMultTextures,
            emitterDistortionTextures) { }

    public Vector3 WorldPos => Transform.Translation;
}

/// <summary>
/// A request to play one or more placed VFX systems live in the viewport (M36). One item for a single
/// selected particle, or many for "Play All". Built on the UI thread by the view-model; consumed on the
/// GL thread by the viewport.
/// </summary>
public sealed record VfxPlayback(IReadOnlyList<VfxPlaybackItem> Items, bool CullByCamera = false);
