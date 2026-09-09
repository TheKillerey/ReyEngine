using System;
using System.Collections.Generic;
using System.Numerics;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

/// <summary>One submesh of a resolved prop mesh (M41): index range + its decoded diffuse (null = untextured).</summary>
public sealed record PropSubmesh(int Start, int Count, TextureImage? Texture)
{
    /// <summary>M678: the other layers the character window binds - mask, gradient, emissive, matcap and
    /// its mask - and the material's render state, resolved by the same ChampionMaterialResolver and
    /// MaterialProfile the window uses. Null layers are unbound; a null material is the pre-M678 draw.</summary>
    public TextureImage? Mask { get; init; }
    public TextureImage? Gradient { get; init; }
    public TextureImage? Emissive { get; init; }
    public TextureImage? MatCap { get; init; }
    public TextureImage? MatCapMask { get; init; }
    public ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial? Material { get; init; }
}

/// <summary>
/// A decoded prop mesh (M41), shared by reference across every placement of the same skin so the viewport
/// uploads each geometry + texture only once. Built off the GL thread; uploaded on it.
/// M54: when the character's skeleton + an idle .anm resolve, the skinning payload rides along so the
/// viewport can play the idle (all placements of a mesh share the pose).
/// </summary>
public sealed record PropMesh(
    string Key,
    float[] Positions, float[] Normals, float[] Uvs, uint[] Indices,
    IReadOnlyList<PropSubmesh> Submeshes)
{
    public ReyEngine.Formats.Meshes.MeshAsset? SknMesh { get; init; }
    /// <summary>M676: the .skn and the skin .bin as read, for a renderer that runs Riot's own shaders over
    /// the prop - the D3D11 path prepares a Dx11CharacterScene from exactly these, as the character window
    /// does. Null for an added mesh, or a prop whose bin was not found; those keep the diffuse-only draw.</summary>
    public byte[]? SknBytes { get; init; }
    public byte[]? SkinBinBytes { get; init; }
    /// <summary>M676: the skin's own <c>skinScale</c> (SkinMeshDataProperties), 1 when unauthored. The game
    /// scales the whole model by it; every placement is composed with it in
    /// <see cref="PropInstanceData.Place"/>.</summary>
    public float SkinScale { get; init; } = 1f;
    public ReyEngine.Formats.Skeletons.SkeletonAsset? Skeleton { get; init; }
    public ReyEngine.Formats.Animation.AnimationClip? IdleClip { get; init; }
    public bool CanAnimate => SknMesh is { CanSkin: true } && Skeleton is not null && IdleClip is not null;

    /// <summary>M636: a mesh that is DRIVEN rather than idling. A prop breathes its idle on the viewport's
    /// shared clock; a playable actor runs, attacks and casts on a clock of its own, so it supplies the
    /// clip and the time it wants posed each frame. Null keeps the idle behaviour exactly. Set by the
    /// playground session on the actor's own PropMesh - which is unique to the actor, so "per mesh" is
    /// "per actor" there. Both renderers read it through <see cref="PoseAt"/>.</summary>
    public Func<(ReyEngine.Formats.Animation.AnimationClip Clip, float Time)>? PoseSource { get; set; }

    /// <summary>The clip and time to skin this frame: the driven pose when one is supplied, otherwise the
    /// idle looping on <paramref name="sharedSeconds"/>. Only meaningful when <see cref="CanAnimate"/>.</summary>
    public (ReyEngine.Formats.Animation.AnimationClip Clip, float Time) PoseAt(float sharedSeconds)
    {
        if (PoseSource is { } driven) return driven();
        var idle = IdleClip!;
        float dur = idle.Duration > 1e-3f ? idle.Duration : 1f;
        return (idle, sharedSeconds % dur);
    }
}

/// <summary>One placed prop: a shared mesh at a world transform.</summary>
public sealed record PropInstanceData(PropMesh Mesh, Matrix4x4 Transform)
{
    /// <summary>M676: a placement of <paramref name="mesh"/> at <paramref name="placement"/> with the skin's
    /// own scale applied first - the game composes skinScale under the placement, so a 0.8 Baron is 0.8 of
    /// the placement, not 0.8 of the world. Both renderers read the composed transform and know nothing
    /// of the scale.</summary>
    public static PropInstanceData Place(PropMesh mesh, Matrix4x4 placement) =>
        new(mesh, mesh.SkinScale is > 0f and not 1f ? Matrix4x4.CreateScale(mesh.SkinScale) * placement : placement);
}

/// <summary>The full set of placed prop meshes to render for the current map (M41).</summary>
public sealed record PropRenderSet(IReadOnlyList<PropInstanceData> Instances);
