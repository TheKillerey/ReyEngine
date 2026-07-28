using System;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.App.Services;

/// <summary>
/// <para>M266: THE animation contract for a <see cref="VfxPlaybackItem"/>. Every viewport builds its
/// simulators here and nowhere else.</para>
///
/// <para>This is an extraction, not a new abstraction, and it is mandatory rather than tidy. The seed below
/// comes from <see cref="HashCode.Combine"/>, which is salted per PROCESS - so two copies of the same
/// expression cannot be validated by comparing screenshots, logs or captures across runs. They can only be
/// shown to agree by both viewports calling one function inside one process. A re-typed formula is
/// unverifiable by construction, which is why it is not re-typed.</para>
///
/// <para>Everything here changes what the SIMULATOR computes. Nothing here touches a GL handle or a D3D11
/// resource - those stay with their own backend.</para>
/// </summary>
public static class VfxPlaybackSim
{
    /// <summary>Build the simulator for one placement, complete. Null when the system has no emitters at
    /// all, which the callers skip.</summary>
    public static VfxParticleSimulator? Create(VfxPlaybackItem item)
    {
        if (item.System.Emitters.Count == 0) return null;
        // A stable placement-specific seed prevents every repeated torch/brazier from animating in lockstep.
        int seed = HashCode.Combine(item.System.PathHash,
            BitConverter.SingleToInt32Bits(item.Transform.M41),
            BitConverter.SingleToInt32Bits(item.Transform.M42),
            BitConverter.SingleToInt32Bits(item.Transform.M43));
        var sim = new VfxParticleSimulator(seed);
        // The Matrix4x4 overload, not the Vector3 one: beyond position it establishes BasePos and the
        // PlacementRight/Up/Forward frame that every arbitraryQuad emitter is oriented in.
        sim.SetSystem(item.System, item.Transform);
        if (item.ColorModulate is { } tint) sim.PlacementTint = tint;   // M203
        if (item.StartDelay > 0f) sim.SetStartDelay(item.StartDelay);   // M91: frame-accurate clip events
        return sim;
    }

    /// <summary>The camera gate's distance term, constants included. Mirrors the GL viewport exactly - the
    /// 6,000-unit floor is what keeps a close-up camera from culling the effect it is looking at.</summary>
    public static float MaxDistanceSquared(float cameraDistance)
    {
        float maxDistance = Math.Max(6000f, cameraDistance * 1.35f);
        return maxDistance * maxDistance;
    }

    /// <summary>
    /// <para>Distance plus a homogeneous-clip frustum test with the 1.25x margin that keeps an effect alive
    /// slightly off screen (its particles can fly inward).</para>
    ///
    /// <para><paramref name="mirroredCamPos"/> must be <c>(-cam.X, cam.Y, cam.Z)</c> and
    /// <paramref name="viewProj"/> must be the MIRROR-INCLUSIVE matrix: world space is unmirrored and the
    /// flip lives in the view, so testing a raw camera position culls the wrong half of the map.</para>
    /// </summary>
    public static bool IsActive(VfxPlaybackItem item, Vector3 mirroredCamPos, float maxDistanceSq,
        in Matrix4x4 viewProj)
    {
        if (Vector3.DistanceSquared(mirroredCamPos, item.WorldPos) > maxDistanceSq) return false;
        var clip = Vector4.Transform(new Vector4(item.WorldPos, 1f), viewProj);
        if (clip.W <= 0f) return false;
        float margin = clip.W * 1.25f;
        if (MathF.Abs(clip.X) > margin || MathF.Abs(clip.Y) > margin || clip.Z < -margin || clip.Z > margin)
            return false;
        return true;
    }

    /// <summary>The pool key the fallback sprite is cached under. Leading space so it can never collide with
    /// a real asset path, which is what every other key in the texture pool is.</summary>
    public const string SoftDotKey = " vfx:softdot";

    /// <summary>A soft radial-gradient RGBA sprite used when a particle's real texture can't be resolved.
    /// Shared so both viewports substitute the SAME pixels - D3D11's own fallback is an opaque 1x1 white,
    /// which turns an unresolved sprite into a solid card rather than a dim placeholder dot.</summary>
    public static byte[] SoftDot(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            // tight core: glow fades out by ~55% of the quad radius so the placeholder reads as a small dot
            float a = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy) * 1.8f, 0f, 1f);
            a *= a;
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255);
        }
        return px;
    }

    /// <summary>
    /// <para>Everything <c>ViewportControl.BindEmitterAssets</c> does that changes what the SIMULATOR
    /// computes, as opposed to which GL handle it binds: the sprite aspect and the CPU-sampled colour
    /// gradient.</para>
    ///
    /// <para>Both land in the packed instance floats - <c>sizeX *= SpriteAspect</c> and a per-particle
    /// gradient multiply - so a backend that skips either disagrees with GL before a single pixel is drawn,
    /// and disagrees in a way that still looks like plausible particles.</para>
    /// </summary>
    public static void ApplySimulationAssets(VfxParticleSimulator sim, VfxPlaybackItem item)
    {
        foreach (var es in sim.Emitters)
        {
            int idx = AuthoredIndex(item.System, es.Def);
            if (idx < 0) continue;

            var img = idx < item.EmitterTextures.Count ? item.EmitterTextures[idx] : null;
            if (img is not null && es.Def.UseTextureAspect)
            {
                float cellWidth = img.Width / Math.Max(1f, es.Def.TexDiv.X);
                float cellHeight = img.Height / Math.Max(1f, es.Def.TexDiv.Y);
                if (cellHeight > 0f) es.SpriteAspect = Math.Clamp(cellWidth / cellHeight, 0.05f, 20f);
            }

            // M68: particleColorTexture is sampled on the CPU, so the emitter gets the decoded RGBA
            // directly rather than a texture handle - which is exactly why it belongs on this side of the
            // split instead of with the backend uploads.
            var colorImg = item.EmitterColorTextures is { } cts && idx < cts.Count ? cts[idx] : null;
            if (colorImg is not null)
            {
                es.ColorGradient = colorImg.Rgba;
                es.ColorGradientW = colorImg.Width;
                es.ColorGradientH = colorImg.Height;
            }
        }
    }

    /// <summary>Map a live emitter state back to its AUTHORED index - the index every per-emitter list on a
    /// <see cref="VfxPlaybackItem"/> is aligned to. By REFERENCE, because SetSystem drops non-visual
    /// emitters but keeps the definition instances, so the two lists differ in length and in order but never
    /// in identity. Returns -1 when unmatched.</summary>
    public static int AuthoredIndex(VfxSystemDefinition system, VfxEmitterDefinition def)
    {
        for (int i = 0; i < system.Emitters.Count; i++)
            if (ReferenceEquals(system.Emitters[i], def)) return i;
        return -1;
    }
}
