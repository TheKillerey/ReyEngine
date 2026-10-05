using System;
using System.Collections.Generic;
using ReyEngine.App.ViewModels;

namespace ReyEngine.App.Views;

/// <summary>
/// M824: the window's half of the Chroma Studio's live BODY RECOLOUR - handing recoloured texels to the renderer that is showing
/// the character. The view model decides WHAT to draw (it holds the originals and the transform); this only knows where the
/// pixels go.
///
/// <para><b>Direct3D 11:</b> the texture pool entry the scene bound is overwritten in place (<c>UpdatePooledTexture</c> keeps every
/// material's binding valid, which swapping the view would not), its mips regenerated, and a frame queued. The pool is shared by
/// every owner of that key, so nothing is disposed here. <b>OpenGL:</b> the host changes the viewport's own images in place and
/// asks the control for an upload and a mip rebuild - the paint tool's route.</para>
/// </summary>
public partial class MeshPreviewWindow
{
    private void WireChromaRecolour(MeshPreviewViewModel vm)
    {
        vm.PushChromaDx11 = items =>
        {
            if (_dx11Closed || _dx11?.IsReady != true) return;
            var touched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, rgba, width, height) in items)
                if (_dx11.Renderer.UpdatePooledTexture(key, rgba, width, height)) touched.Add(key);
            foreach (string key in touched) _dx11.Renderer.RegeneratePooledMips(key);
            if (touched.Count > 0) QueueDx11Frame();
        };
        vm.QueueGlTextureUpdate = PreviewViewport.QueueTextureUpdate;
        vm.RebuildGlTextureMips = PreviewViewport.RequestMipRebuild;
    }
}
