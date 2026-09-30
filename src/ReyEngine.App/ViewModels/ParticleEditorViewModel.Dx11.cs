using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M783: the D3D11 half of the Particle Editor's 3D preview - the same particle path
/// (<see cref="Services.D3D11MapParticles"/>) the map viewport and the character window already draw
/// through, in place of the GL approximation (<see cref="ReyEngine.Rendering.Vfx.VfxParticleSimulator"/> +
/// <c>ViewportControl</c>'s billboard renderer) the editor's preview has used since M46.
///
/// <para>The user's own complaint named the gap directly: a mesh emitter drew as "shattered triangles" in
/// this editor while the same system, through the map viewport's D3D11 path, drew correctly - because the
/// GL viewport only passes mesh indices when an animation resolved (ViewportControl.cs), while the D3D11
/// driver always does. Rather than chase every such divergence twice, the preview now draws through the
/// SAME driver the viewport trusts, and GL becomes the fallback: unavailable device, no shader cache, or a
/// character host attached (which the D3D11 path does not pose for emission-surface sampling here - see
/// ParticleEditorView.Dx11.cs for where that check runs, every time the toggle is asked to turn on).</para>
///
/// <para>This half holds only the state the window's frame loop reads and writes - the toggle, the status
/// line, and the two things the host (MainWindowViewModel) supplies: a shader cache and a log sink. The
/// window (ParticleEditorView.Dx11.cs) owns the device, the frame loop and the presentation, exactly as
/// MeshPreviewWindow.Dx11.cs does for the character window.</para>
/// </summary>
public sealed partial class ParticleEditorViewModel
{
    /// <summary>Off at construction; <c>MainWindowViewModel</c> sets this once, at wiring time, from the
    /// SAME setting the map viewport's own D3D11 default reads (<c>!Settings.UseOpenGlViewport</c>) - so a
    /// user who already chose GL globally opens the Particle Editor on GL too, and everyone else opens on
    /// D3D11, matching the map viewport they compared it against.</summary>
    [ObservableProperty] private bool _useDx11Preview;

    [ObservableProperty] private string _dx11Status = "";

    /// <summary>
    /// The shader cache the D3D11 particle driver resolves its pipelines against. A <c>Func</c> rather than
    /// a value pushed once, unlike the character window's <c>Dx11ShaderCache</c> field: the Particle Editor
    /// can be the very first D3D11 surface the user opens in a session (no map, no character loaded yet),
    /// so the cache is asked for lazily, on the frame the window first tries to render, rather than assumed
    /// already open.
    /// </summary>
    public Func<ReyEngine.Formats.Shaders.ShaderCacheReader?>? ResolveDx11ShaderCache { get; set; }

    /// <summary>The editor console, exactly like <c>MeshPreviewViewModel.LogDx11</c> - this view model has
    /// no logger of its own and should not grow one.</summary>
    public Action<string, string>? LogDx11 { get; set; }

    /// <summary>The status describes a renderer that is no longer drawing once the toggle is off - leaving
    /// it up would read as though D3D11 were still live while GL draws underneath it.</summary>
    partial void OnUseDx11PreviewChanged(bool value)
    {
        if (!value) Dx11Status = "";
    }

    /// <summary>
    /// M800: the status line for a frame the D3D11 surface could not render, <paramref name="why"/> being its
    /// <c>LastError</c>.
    ///
    /// <para>"no shader loaded" is the renderer's name for "no emitter produced a material". For a system with
    /// no VISUAL emitter - a stub, a fully disabled one, or one authored in Riot's component (Shimmer) format,
    /// which ReyEngine does not simulate - that is the correct outcome and not a fault, and the line used to
    /// read <c>render failed: no shader loaded</c> for it, as if the device had broken. So exactly that pair
    /// - the renderer's "nothing built" error AND a playback that has no visual emitter - becomes "Nothing to
    /// draw" plus the reason. Every other failure keeps its message, including "no shader loaded" for a
    /// system that DOES have a visual emitter (its pipelines failed to build, which is worth a red flag).</para>
    /// </summary>
    public string Dx11RenderFailedStatus(string why)
    {
        if (why == ReyEngine.Rendering.D3D11.ShaderPreviewRenderer.NoShaderLoaded
            && Playback is { Items.Count: > 0 } playback
            && playback.Items.All(static i => ReyEngine.Formats.Vfx.VfxNothingToDraw.Applies(i.System)))
            return ReyEngine.Formats.Vfx.VfxNothingToDraw.Status(playback.Items[0].System)!;
        return "render failed: " + why;
    }
}
