using System.Numerics;
using ReyEngine.Core.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ReyEngine.Core.Assets;

/// <summary>The placement kinds the viewport marks. The order is part of the D3D11 glyph array's
/// indexing, so append rather than reorder.</summary>
public enum ViewportIcon { Particle = 0, Sound = 1, Prop = 2, Probe = 3, Light = 4 }

/// <summary>One decoded icon, ready to upload: straight (non-premultiplied) RGBA, row-major, top-down.</summary>
public sealed record ViewportIconImage(byte[] Rgba, int Size);

/// <summary>
/// M657: the viewport's placement icons, as painted art rather than as code.
///
/// <para>They live HERE, in Core, because both renderers need them and the two rendering assemblies
/// deliberately do not reference each other (see the note in ReyEngine.Rendering.D3D11.csproj: keeping
/// Direct3D out of the OpenGL renderer means the two cannot grow into each other). Core is the only
/// place both can see.</para>
///
/// <para>Embedded rather than copied next to the exe, which keeps the property the drawn glyphs had:
/// there is no missing-file failure mode and no packaging step that can ship a build with the icons
/// silently absent. Both renderers still keep their drawn glyph as a fallback, so a decode failure
/// costs fidelity and not markers.</para>
///
/// <para>Stored at 256x256. The marker draws at a few dozen pixels and is capped at a fraction of the
/// viewport height, so 256 is roughly four times the largest size it is ever seen at - enough headroom
/// for a high-DPI display, against 1254x1254 sources that would have cost 6 MB of VRAM each.</para>
/// </summary>
public static class ViewportIcons
{
    private const string Prefix = "ReyEngine.Core.Assets.ViewportIcons.";

    private static readonly string[] Files =
        { "particles.png", "sounds.png", "mobs-props.png", "reflection-probes.png", "lights.png" };

    /// <summary>
    /// M657: the largest a placement marker may get on screen, as a fraction of the viewport HEIGHT.
    ///
    /// <para>A marker is a WORLD-sized billboard, which is right at a distance and wrong up close: fly in
    /// to move a particle and its icon grows without limit until it covers the thing being placed. Both
    /// renderers cap it here. Nothing is done at the FAR end - shrinking with distance is what keeps a
    /// zoomed-out map from becoming a wall of icons, and that half was never the problem.</para>
    ///
    /// <para>A fraction rather than a pixel count, so the cap means the same thing on a 4K or high-DPI
    /// display where a fixed pixel size would read as half the icon. Zero disables the cap.</para>
    /// </summary>
    public static float MaxHeightFraction { get; set; } = 0.05f;

    /// <summary>
    /// M657: the world size a marker should actually be drawn at, given where it is and how big the
    /// viewport is. Never larger than the supplied size - this only ever shrinks a marker that would
    /// otherwise cover <see cref="MaxHeightFraction"/> of the screen.
    ///
    /// <para>Measured as two clip-space points rather than from a projection term, so it needs nothing
    /// but the view-projection and the viewport height, and works for any projection. The OpenGL vertex
    /// shader does exactly this arithmetic per vertex; D3D11 builds its quads on the CPU and calls this.
    /// Keeping them the same shape is the point: two backends that cap differently is a bug that only
    /// shows up as "it looks wrong in the other renderer".</para>
    /// </summary>
    public static float CapWorldSize(Vector3 position, Vector3 cameraUp, float worldSize,
        Matrix4x4 viewProjection, float viewportHeightPx) =>
        ScreenSize.CapAtPixels(position, cameraUp, worldSize, viewProjection, viewportHeightPx,
            viewportHeightPx * MaxHeightFraction);

    private static readonly ViewportIconImage?[] Cache = new ViewportIconImage?[Files.Length];
    private static readonly bool[] Tried = new bool[Files.Length];
    private static readonly object Gate = new();

    /// <summary>Decoded once per icon and kept. Null when the resource is missing or will not decode -
    /// the caller falls back to its drawn glyph rather than showing nothing.</summary>
    public static ViewportIconImage? Load(ViewportIcon icon)
    {
        int i = (int)icon;
        if (i < 0 || i >= Files.Length) return null;
        lock (Gate)
        {
            if (Tried[i]) return Cache[i];
            Tried[i] = true;
            try
            {
                var assembly = typeof(ViewportIcons).Assembly;
                using var stream = assembly.GetManifestResourceStream(Prefix + Files[i]);
                if (stream is null) return null;
                using var image = Image.Load<Rgba32>(stream);
                var rgba = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(rgba);
                Cache[i] = new ViewportIconImage(rgba, image.Width);
            }
            catch { Cache[i] = null; }
            return Cache[i];
        }
    }
}
