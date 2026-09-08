using System;

namespace ReyEngine.App.Services;

/// <summary>
/// M660: one reference guard per overlay the D3D11 viewport publishes.
///
/// <para><b>Why this exists as a type rather than a few fields.</b> The overlays are multi-megabyte
/// float arrays republished wholesale, so each is uploaded only when its array is a different instance
/// from the one last sent. That is right — but the guards were written inline, and the navgrid's upload
/// ended up INSIDE the bucket grid's <c>if</c>. Nested there it could only run when the BUCKET GRID
/// changed identity, so switching the navgrid overlay on never reached D3D11 at all and the layer never
/// drew. The face-edit selection sat in the same block with the same fault.</para>
///
/// <para>A guard that silently gates something other than what it names is invisible in review and
/// invisible in a screenshot of the other renderer. Making the association explicit — one enum member
/// per overlay, one call per overlay — is what makes it checkable, and
/// <c>Dx11OverlayGateTests</c> checks it.</para>
/// </summary>
public sealed class Dx11OverlayGate
{
    public enum Overlay
    {
        BucketGrid = 0,
        NavGridCells = 1,
        NavGridLayers = 2,
        SelectedFaces = 3,
    }

    private readonly object?[] _last = new object?[4];

    /// <summary>
    /// True when <paramref name="value"/> is a DIFFERENT instance from the one last published for this
    /// overlay, and remembers it. Reference equality on purpose: these arrays are rebuilt wholesale, and
    /// comparing their contents would cost more than the upload it is trying to avoid.
    /// </summary>
    public bool Changed(Overlay overlay, object? value)
    {
        int i = (int)overlay;
        if ((uint)i >= _last.Length) return true;
        if (ReferenceEquals(_last[i], value)) return false;
        _last[i] = value;
        return true;
    }

    /// <summary>Forget everything, so the next frame republishes. For a renderer that has been torn down
    /// and rebuilt, where the GPU no longer holds what this thinks it sent.</summary>
    public void Reset() => Array.Clear(_last);
}
