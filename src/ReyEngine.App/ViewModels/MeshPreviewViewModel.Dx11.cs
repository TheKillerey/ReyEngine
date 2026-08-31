using System;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.App.Services;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M618: drawing the character preview with Riot's own compiled shaders.
///
/// <para>The GL viewport approximates them; D3D11 runs the real ones. The two are siblings that swap by
/// visibility, the pattern the map viewport has used since M248 — nothing GL is removed, and unticking
/// the box puts everything back.</para>
///
/// <para>This half holds only what the renderer needs and nothing about how it draws: the prepared scene,
/// the skeleton, and the bone palette rebuilt from whatever the animation clock is showing. The window
/// owns the device, the frame loop and the presentation.</para>
/// </summary>
public sealed partial class MeshPreviewViewModel
{
    [ObservableProperty] private bool _useDx11Preview;
    [ObservableProperty] private string _dx11Status = "";

    /// <summary>The resolved scene, or null when this subject is not a character the resolver understood.
    /// Set off the UI thread by the host as part of loading a skin.</summary>
    public PreparedCharacterScene? Dx11Scene { get; private set; }

    /// <summary>Bumped whenever a new scene arrives, so the window knows to commit it once rather than
    /// every frame. A reference check is not enough: the same scene can be handed over again after an edit.</summary>
    public int Dx11SceneRevision { get; private set; }

    public void SetDx11Scene(PreparedCharacterScene? scene, string status)
    {
        Dx11Scene = scene;
        Dx11SceneRevision++;
        Dx11Status = status;
    }

    /// <summary>The bone palette for this instant, or null when there is no skeleton — in which case the
    /// renderer keeps its bind-pose constant, which is exactly right for anything unskinned.</summary>
    public Matrix4x4[]? CurrentBonePalette()
    {
        // The SAME skeleton and the SAME clock the GL path skins with, so the two renderers cannot show
        // the character at two different instants.
        if (Skeleton is not { } skeleton) return null;
        return BonePalette.Build(skeleton, CurrentAnimation, (float)AnimationTime);
    }

    /// <summary>M619: the palette and the skeleton overlay from ONE walk of the joints. Two calls would
    /// evaluate the clip twice per frame for the same instant, which is pure waste and one refactor away
    /// from being two different instants.</summary>
    public (Matrix4x4[]? Palette, float[]? Bones) CurrentPose(bool wantBones)
    {
        if (Skeleton is not { } skeleton) return (null, null);
        var (palette, segments) = BonePalette.BuildWithSegments(
            skeleton, CurrentAnimation, (float)AnimationTime, wantBones);
        if (!wantBones || segments.Length == 0) return (palette, null);

        // The segments are joint positions in MODEL space. The mesh beside them travels through the
        // palette; without the same transform here the skeleton stays at the origin while the character
        // it belongs to walks away - which is exactly what M614 had to fix on the GL side.
        var world = ModelWorld;
        if (!world.IsIdentity)
            for (int i = 0; i + 2 < segments.Length; i += 3)
            {
                var p = Vector3.Transform(new Vector3(segments[i], segments[i + 1], segments[i + 2]), world);
                segments[i] = p.X; segments[i + 1] = p.Y; segments[i + 2] = p.Z;
            }
        return (palette, segments);
    }

    /// <summary>The shader cache the D3D11 particle driver needs. Supplied by the host, which owns it.</summary>
    public Formats.Shaders.ShaderCacheReader? Dx11ShaderCache { get; set; }

    /// <summary>M620: where the character is standing, as one matrix. The SAME composition the GL
    /// viewport builds (scale, then facing, then position), so control mode moves the character the same
    /// way whichever renderer is drawing it.</summary>
    public Matrix4x4 ModelWorld =>
        Matrix4x4.CreateScale((float)ModelScale)
        * Matrix4x4.CreateRotationY((float)CharacterYaw)
        * Matrix4x4.CreateTranslation(CharacterPosition);

    partial void OnUseDx11PreviewChanged(bool value)
    {
        if (!value) { Dx11Status = ""; return; }

        // M620: only when nothing more specific has been said. This used to overwrite unconditionally,
        // so the real reason - which the host had already put here - was replaced by a guess at it, and
        // "the shader cache never opened" read as "this is not a character".
        if (Dx11Scene is null && string.IsNullOrWhiteSpace(Dx11Status))
            Dx11Status = "No D3D11 scene for this subject - it resolved no materials, or it is not a character.";
    }
}
