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

    partial void OnUseDx11PreviewChanged(bool value)
    {
        if (!value) { Dx11Status = ""; return; }
        if (Dx11Scene is null)
            Dx11Status = "No D3D11 scene for this subject - it resolved no materials, or it is not a character.";
    }
}
